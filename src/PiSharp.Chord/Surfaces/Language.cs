// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/surfaces/{language,format}.ts.
using System.Globalization;

namespace PiSharp.Chord.Surfaces;

public sealed record MessagesReport(string Kind, string Namespace, string Key, string? Locale = null,
    string? Used = null, string? Plugin = null, Exception? Error = null, string? Expected = null);
public sealed record Language(string Locale, string Dir, IReadOnlyList<string> Chain, TranslationIndex Translations,
    Action<MessagesReport> Report);

public static class Languages
{
    // Rendering is synchronous. Thread-local nesting prevents unrelated hosts/threads from inheriting a locale.
    [ThreadStatic] private static Language? rendering;
    public static Language? Current => rendering;
    public static T In<T>(Language? language, Func<T> render)
    {
        var previous = rendering; rendering = language;
        try { return render(); } finally { rendering = previous; }
    }
    public static Language Create(string locale, TranslationIndex translations, Action<MessagesReport>? report = null)
    {
        var canonical = SurfaceTranslations.CanonicalLocale(locale) ?? throw new ArgumentException("Invalid language tag", nameof(locale));
        return new(canonical, DirectionOf(canonical), ChainOf(canonical), translations, Once(report ?? (_ => { })));
    }
    internal static Action<MessagesReport> Once(Action<MessagesReport> report)
    {
        var seen = new HashSet<(string, string, string?, string)>(); var gate = new object();
        return each =>
        {
            lock (gate) if (!seen.Add((each.Kind, each.Namespace, each.Locale, each.Key))) return;
            report(each);
        };
    }
    public static string ResolveLocale(string? pick, IReadOnlyList<string> preferred, IReadOnlyList<string> offered) =>
        MatchLocale(pick is null ? [] : [pick], offered) ?? MatchLocale(preferred, offered) ??
            (offered.Contains("en", StringComparer.Ordinal) ? "en" : offered.FirstOrDefault() ?? "en");
    public static string? MatchLocale(IReadOnlyList<string> preferred, IReadOnlyList<string> offered)
    {
        foreach (var wanted in preferred)
        {
            var locale = SurfaceTranslations.CanonicalLocale(wanted);
            if (locale is null) continue;
            var exact = ChainOf(locale).FirstOrDefault(offered.Contains);
            if (exact is not null) return exact;
            var language = locale.Split('-')[0];
            var related = offered.FirstOrDefault(each => each.Split('-')[0] == language);
            if (related is not null) return related;
        }
        return null;
    }
    public static IReadOnlyList<string> ChainOf(string locale)
    {
        var parts = locale.Split('-');
        return Enumerable.Range(0, parts.Length).Select(index => string.Join('-', parts.Take(parts.Length - index))).ToArray();
    }
    private static string DirectionOf(string locale)
    {
        var script = locale.Split('-').Skip(1).FirstOrDefault(part => part.Length == 4);
        if (script is not null) return new[] { "Adlm", "Arab", "Hebr", "Mand", "Nkoo", "Rohg", "Samr", "Syrc", "Thaa", "Yezi" }.Contains(script) ? "rtl" : "ltr";
        try { return CultureInfo.GetCultureInfo(locale).TextInfo.IsRightToLeft ? "rtl" : "ltr"; }
        catch (CultureNotFoundException) { return "ltr"; }
    }
}

/// <summary>.NET locale formatting scoped to the current synchronous message render.</summary>
public static class Format
{
    private static CultureInfo Culture
    {
        get
        {
            if (Languages.Current is not { } language) return CultureInfo.CurrentCulture;
            try { return CultureInfo.GetCultureInfo(language.Locale); }
            catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
        }
    }
    public static string Number(double value, string? format = null) => value.ToString(format ?? "#,0.###", Culture);
    public static string Number(decimal value, string? format = null) => value.ToString(format ?? "#,0.###", Culture);
    public static string Time(DateTimeOffset at, string? format = null, TimeZoneInfo? zone = null) =>
        TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Local).ToString(format ?? "t", Culture);
    public static string Time(long milliseconds, string? format = null, TimeZoneInfo? zone = null) =>
        Time(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds), format, zone);
    public static string Time(string iso, string? format = null, TimeZoneInfo? zone = null) =>
        Time(DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), format, zone);
}
