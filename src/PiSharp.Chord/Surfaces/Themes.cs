// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/surfaces/themes.ts.
using System.Globalization;
using PiSharp.Chord.Host;
using PiSharp.Chord.Registries;

namespace PiSharp.Chord.Surfaces;

public enum ThemeMode { Dark, Light }
public enum ColorDepth { TrueColor, Indexed256 }
public enum ColorLayer { Foreground, Background }
public sealed record Theme(string Id, string Name, ThemeMode Mode, IReadOnlyDictionary<string, string> Colors,
    IReadOnlyDictionary<string, string>? Web = null, IReadOnlyDictionary<string, string>? Tui = null);
public sealed record ThemePick(string? Dark = null, string? Light = null);

public static class Themes
{
    public static IReadOnlyList<string> BaseRoles { get; } = Array.AsReadOnly(new[] { "accent", "bg", "fg", "muted", "error", "warning", "success" });
    public static Registry<Theme, IReadOnlyList<Theme>> Registry { get; } =
        Registries.Registry.Define<Theme, IReadOnlyList<Theme>>("chord.themes", new() { Version = 1 });
    public static IReadOnlyDictionary<ThemeMode, Theme> FallbackThemes { get; } = new Dictionary<ThemeMode, Theme>
    {
        [ThemeMode.Dark] = Make("chord.dark", "Dark", ThemeMode.Dark, ["#4c9aff", "#16181d", "#e6e6e6", "#8a8f98", "#ff6b6b", "#f0b429", "#3ecf8e"]),
        [ThemeMode.Light] = Make("chord.light", "Light", ThemeMode.Light, ["#0b62d6", "#ffffff", "#1c1e21", "#6b7280", "#c62828", "#b26a00", "#1b7f4b"]),
    };
    private static Theme Make(string id, string name, ThemeMode mode, string[] colors) =>
        new(id, name, mode, BaseRoles.Zip(colors).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal));
    public static Facet Facet() => Host.Facet.Define((host, _) => host.Serve(Registry,
        new RegistryProvider<Theme, IReadOnlyList<Theme>> { Compose = (table, _) => table.Select(entry => entry.Value).ToArray() }));
    public static Theme Active(IReadOnlyList<Theme> themes, ThemePick pick, ThemeMode mode)
    {
        var wanted = mode == ThemeMode.Dark ? pick.Dark : pick.Light;
        return themes.FirstOrDefault(theme => theme.Mode == mode && theme.Id == wanted) ??
            themes.FirstOrDefault(theme => theme.Mode == mode) ?? FallbackThemes[mode];
    }
    public static IReadOnlyDictionary<string, string> Variables(Theme theme)
    {
        var result = theme.Colors.ToDictionary(pair => "--" + pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (theme.Web is not null) foreach (var (key, value) in theme.Web) result["--" + key] = value;
        return result;
    }
    public static string TerminalColor(Theme theme, string role, ColorDepth depth, ColorLayer layer = ColorLayer.Foreground)
    {
        var color = theme.Tui?.GetValueOrDefault(role) ?? theme.Colors.GetValueOrDefault(role) ?? FallbackThemes[theme.Mode].Colors.GetValueOrDefault(role);
        var prefix = layer == ColorLayer.Foreground ? "38" : "48";
        if (color is not null && color.Length is >= 1 and <= 3 && color.All(char.IsAsciiDigit) &&
            int.TryParse(color, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < 256) return $"{prefix};5;{color}";
        if (color is null || color.Length != 7 || color[0] != '#' || !int.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return layer == ColorLayer.Foreground ? "39" : "49";
        var r = rgb >> 16; var g = rgb >> 8 & 255; var b = rgb & 255;
        return depth == ColorDepth.TrueColor ? $"{prefix};2;{r};{g};{b}" : $"{prefix};5;{Nearest256(r, g, b)}";
    }
    private static int Nearest256(int r, int g, int b)
    {
        int[] cube = [0, 95, 135, 175, 215, 255];
        int Level(int value) => Enumerable.Range(0, 6).MinBy(index => Math.Abs(cube[index] - value));
        var cr = Level(r); var cg = Level(g); var cb = Level(b);
        var grey = Math.Clamp((int)Math.Floor(((r + g + b) / 3.0 - 8) / 10 + 0.5), 0, 23);
        var greyValue = 8 + grey * 10;
        static int Square(int value) => value * value;
        return Square(greyValue - r) + Square(greyValue - g) + Square(greyValue - b) <
            Square(cube[cr] - r) + Square(cube[cg] - g) + Square(cube[cb] - b) ? 232 + grey : 16 + 36 * cr + 6 * cg + cb;
    }
}
