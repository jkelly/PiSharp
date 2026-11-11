// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/surfaces/translations.ts.
using System.Globalization;
using System.Text.RegularExpressions;
using PiSharp.Chord.Host;
using PiSharp.Chord.Registries;

namespace PiSharp.Chord.Surfaces;

public sealed record Translation(string Namespace, string Locale, Catalog Messages, bool Override = false);
public sealed record TranslationIndex(IReadOnlyDictionary<string, IReadOnlyDictionary<string, TableItem<Translation>>> Catalogs,
    IReadOnlyList<string> Locales);

public static class SurfaceTranslations
{
    public static Registry<IReadOnlyList<Translation>, TranslationIndex> Registry { get; } =
        Registries.Registry.Define<IReadOnlyList<Translation>, TranslationIndex>("chord.translations", new() { Version = 1 });
    public static TranslationIndex None { get; } = new(new Dictionary<string, IReadOnlyDictionary<string, TableItem<Translation>>>(StringComparer.Ordinal), []);
    public static Facet Facet() => Host.Facet.Define((host, _) => host.Serve(Registry,
        new RegistryProvider<IReadOnlyList<Translation>, TranslationIndex> { Compose = Compose }));

    public static TranslationIndex Compose(IReadOnlyList<RegistryEntry<IReadOnlyList<Translation>>> table, ComposeContext context)
    {
        var catalogs = new Dictionary<string, Dictionary<string, TableItem<Translation>>>(StringComparer.Ordinal);
        var locales = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in table.OrderBy(entry => entry.Plugin, StringComparer.Ordinal))
            foreach (var translation in entry.Value)
            {
                var locale = CanonicalLocale(translation.Locale);
                if (locale is null)
                { context.Report(new($"\"{translation.Locale}\" is no locale ({translation.Namespace})", [entry.Plugin])); continue; }
                locales.Add(locale);
                if (!catalogs.TryGetValue(translation.Namespace, out var byLocale)) catalogs.Add(translation.Namespace, byLocale = new(StringComparer.Ordinal));
                if (!byLocale.TryGetValue(locale, out var taken) || translation.Override && !taken.Value.Override)
                    byLocale[locale] = new(translation, entry.Plugin);
                else if (translation.Override == taken.Value.Override)
                    context.Report(new($"{translation.Namespace} in {locale} is contributed twice", [taken.Plugin, entry.Plugin]));
            }
        return new(catalogs.ToDictionary(entry => entry.Key,
            entry => (IReadOnlyDictionary<string, TableItem<Translation>>)entry.Value, StringComparer.Ordinal), locales.ToArray());
    }

    /// <summary>Canonical BCP 47 casing and .NET/ICU language aliases; malformed tags are refused.</summary>
    public static string? CanonicalLocale(string? locale)
    {
        if (locale is null || !Regex.IsMatch(locale, "^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$", RegexOptions.CultureInvariant)) return null;
        var parts = locale.Split('-');
        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extended = false;
        for (var i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length == 1)
            {
                if (i == parts.Length - 1 || !extensions.Add(parts[i])) return null;
                extended = true;
                if (parts[i].Equals("x", StringComparison.OrdinalIgnoreCase)) break;
                if (parts[i + 1].Length < 2) return null;
            }
            else if (!extended && parts[i].Length >= 5 && !variants.Add(parts[i])) return null;
        }
        try
        {
            var canonical = CultureInfo.GetCultureInfo(locale).Name;
            if (!string.IsNullOrEmpty(canonical)) parts = canonical.Split('-');
        }
        catch (CultureNotFoundException) { /* Structurally valid private/extension tags need no installed culture. */ }
        parts[0] = parts[0].ToLowerInvariant() switch { "iw" => "he", "in" => "id", "ji" => "yi", var language => language };
        extended = false;
        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 1) extended = true;
            parts[i] = !extended && part.Length == 4 && part.All(char.IsAsciiLetter)
                ? char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()
                : !extended && part.Length == 2 && part.All(char.IsAsciiLetter) ? part.ToUpperInvariant() : part.ToLowerInvariant();
        }
        return string.Join('-', parts);
    }
}
