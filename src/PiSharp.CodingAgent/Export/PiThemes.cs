// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/src/modes/interactive/theme/theme.ts (Theme colors, theme
// loading, getThemeByName, initTheme, detectTerminalTheme, resolveThemeSetting, getResolvedThemeColors, getThemeExportColors),
// theme/theme-tokens.ts (token order and fallbacks), theme/theme-schema.ts (validateThemeJson), theme/dark.json and theme/light.json
// (embedded byte-for-byte), src/main.ts (the validator is installed before any theme loads) and src/config.ts (getAgentDir,
// getCustomThemesDir).
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.CodingAgent.Export;

/// <summary>A loaded theme: concrete token colors, tokens set to the terminal default, faint tokens and its own appearance.</summary>
public sealed class PiTheme
{
    private readonly List<KeyValuePair<string, PiColor>> concrete = [];
    private readonly List<string> defaultForegrounds = [], defaultBackgrounds = [];
    private readonly HashSet<string> dim;
    private readonly List<(string Token, object Value, bool Background)> rawTokens = [];
    /// <summary>Resolved token values in token order (hex/ok*() strings, palette indices as double, "" for the terminal default); the
    /// interactive Theme builds its escape sequences from them.</summary>
    public IReadOnlyList<(string Token, object Value, bool Background)> RawTokens => rawTokens;
    /// <summary>Foreground tokens rendered faint (SGR 2).</summary>
    public IReadOnlyCollection<string> DimTokens => dim;
    public string? Name { get; }
    public string? SourcePath { get; }
    /// <summary>Declared in the theme JSON or detected from its colors; null for themes without usable colors.</summary>
    public string? OwnAppearance { get; }

    internal PiTheme(IReadOnlyList<KeyValuePair<string, object>> foregrounds, IReadOnlyList<KeyValuePair<string, object>> backgrounds,
        string? name, string? sourcePath, string? appearance, IEnumerable<string>? dimTokens = null)
    {
        Name = name; SourcePath = sourcePath; dim = new(dimTokens ?? [], StringComparer.Ordinal);
        // theme-tokens.ts THEME_TOKENS order: optional tokens fall back in token-file order.
        var fg = WithFallbacks(foregrounds, ("scrollbarTrack", "muted"), ("scrollbarThumb", "text"), ("searchMatchText", "text"), ("thinkingMax", "thinkingXhigh"));
        var bg = WithFallbacks(backgrounds, ("searchMatchBg", "selectedBg"));
        var concreteForegrounds = new List<PiColor>(); var concreteBackgrounds = new List<PiColor>();
        void Add(string token, object value, bool background)
        {
            rawTokens.Add((token, value, background));
            if (value is "") { (background ? defaultBackgrounds : defaultForegrounds).Add(token); return; }
            var color = value switch { double index => PiColors.Parse(index), string text => PiColors.Parse(text),
                _ => throw new ArgumentException($"Invalid color value: {Js.ToJsString(value)}") };
            concrete.Add(new(token, color)); (background ? concreteBackgrounds : concreteForegrounds).Add(color);
        }
        foreach (var (token, value) in fg) Add(token, value, false);
        foreach (var (token, value) in bg) Add(token, value, true);
        OwnAppearance = appearance ?? DetectAppearance(concreteForegrounds, concreteBackgrounds);
    }

    private static List<KeyValuePair<string, object>> WithFallbacks(IReadOnlyList<KeyValuePair<string, object>> source, params (string Token, string From)[] fallbacks)
    {
        var result = source.ToList();
        foreach (var (token, from) in fallbacks)
        {
            if (result.Any(pair => pair.Key == token)) continue;
            var fallback = result.FirstOrDefault(pair => pair.Key == from);
            if (fallback.Key is null) throw new ArgumentException($"Theme token {token} has unresolved fallback {from}");
            result.Add(new(token, fallback.Value));
        }
        return result;
    }

    private static double? AverageLightness(List<PiColor> colors)
    {
        var fixedColors = colors.Where(color => color is not PiColor.Indexed { Index: < 16 }).ToList();
        if (fixedColors.Count == 0) return null;
        return fixedColors.Aggregate(0d, (sum, color) => sum + PiColors.ToOklch(color).L) / fixedColors.Count;
    }

    private static string? DetectAppearance(List<PiColor> foregrounds, List<PiColor> backgrounds)
    {
        var fg = AverageLightness(foregrounds); var bg = AverageLightness(backgrounds);
        if (fg is { } f && bg is { } b) return b < f ? "dark" : "light";
        if (bg is { } onlyBackground) return onlyBackground < 0.5 ? "dark" : "light";
        if (fg is { } onlyForeground) return onlyForeground > 0.5 ? "dark" : "light";
        return null;
    }

    /// <summary>The background the theme is designed for, or the terminal's appearance.</summary>
    public string Appearance(PiThemeHost host) => OwnAppearance ?? host.GetTerminalTheme();

    /// <summary>Theme.colors: concrete colors for all tokens in property order; terminal defaults from the report or the guess.</summary>
    public IReadOnlyList<KeyValuePair<string, PiColor>> Colors(PiThemeHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var dark = Appearance(host) == "dark";
        var terminal = host.TerminalColors;
        PiColor foreground = terminal.Foreground is { } f ? PiColors.Rgb(f.R, f.G, f.B) : dark ? PiColors.Parse("#e5e5e7") : PiColors.Parse("#000000");
        PiColor background = terminal.Background is { } b ? PiColors.Rgb(b.R, b.G, b.B) : dark ? PiColors.Parse("#000000") : PiColors.Parse("#ffffff");
        var colors = concrete.ToList();
        foreach (var token in defaultForegrounds) colors.Add(new(token, foreground));
        foreach (var token in defaultBackgrounds) colors.Add(new(token, background));
        for (var i = 0; i < colors.Count; i++)
            if (dim.Contains(colors[i].Key)) colors[i] = new(colors[i].Key, PiColors.Mix(colors[i].Value, background, 0.4));
        return colors;
    }
}

/// <summary>
/// The theme.ts module state the exporter reads: current theme (initTheme), registered themes, the terminal's reports, the
/// environment (COLORFGBG, PI_CODING_AGENT_DIR) and whether theme JSON is validated (pi's main installs the validator).
/// Without a terminal (RPC, print, --export) nothing is reported and the system theme uses ANSI palette indices.
/// </summary>
public sealed class PiThemeHost
{
    public const string SystemThemeName = SystemTheme.Name;
    private static readonly Lazy<IReadOnlyDictionary<string, string>> BuiltinThemes = new(() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["dark"] = SessionHtmlExport.ReadAsset("dark.json"), ["light"] = SessionHtmlExport.ReadAsset("light.json")
    });

    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;
    /// <summary>getAgentDir override; null derives it from PI_CODING_AGENT_DIR or ~/.pi/agent.</summary>
    public string? AgentDirectory { get; init; }
    public string HomeDirectory { get; init; } = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
    /// <summary>setRegisteredThemes: name to theme file (extension and package themes).</summary>
    public IReadOnlyDictionary<string, string> RegisteredThemePaths { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public TerminalColorReport TerminalColors { get; init; } = new();
    /// <summary>The terminal's light/dark report (mode 2031), if any.</summary>
    public string? TerminalColorScheme { get; init; }
    public bool TerminalColorsPending { get; init; }
    /// <summary>setThemeJsonValidator(validateThemeJson): pi's main installs it before any theme loads.</summary>
    public bool ValidateThemeJson { get; init; } = true;
    /// <summary>currentThemeName; null until <see cref="InitTheme"/> runs.</summary>
    public string? CurrentThemeName { get; set; }

    public string GetAgentDirectory()
    {
        if (AgentDirectory is not null) return AgentDirectory;
        var configured = Environment("PI_CODING_AGENT_DIR");
        if (!string.IsNullOrEmpty(configured))
        {
            if (configured == "~") return HomeDirectory;
            if (configured.StartsWith("~/", StringComparison.Ordinal) || OperatingSystem.IsWindows() && configured.StartsWith("~\\", StringComparison.Ordinal))
                return Path.Combine(HomeDirectory, configured[2..]);
            return configured;
        }
        return Path.Combine(HomeDirectory, ".pi", "agent");
    }

    public string GetCustomThemesDirectory() => Path.Combine(GetAgentDirectory(), "themes");

    /// <summary>detectColorFgBgTheme.</summary>
    public string? DetectColorFgBgTheme()
    {
        var value = Environment("COLORFGBG");
        if (value is null) return null;
        var bg = Js.Trim(value.Split(';')[^1]);
        if (!Regex.IsMatch(bg, @"^[0-9]{1,2}\z", RegexOptions.CultureInvariant)) return null;
        var index = int.Parse(bg, System.Globalization.CultureInfo.InvariantCulture);
        if (index > 15) return null;
        return index <= 6 || index == 8 ? "dark" : "light";
    }

    /// <summary>getTerminalTheme: the reported background decides, then the light/dark report, then COLORFGBG, then dark.</summary>
    public string GetTerminalTheme()
    {
        if (TerminalColors.Background is { } background) return SystemTheme.TerminalAppearance(background, TerminalColors.Foreground);
        return TerminalColorScheme ?? DetectColorFgBgTheme() ?? "dark";
    }

    /// <summary>parseAutoThemeSetting ("light/dark").</summary>
    public static (string LightTheme, string DarkTheme)? ParseAutoThemeSetting(string? setting)
    {
        if (string.IsNullOrEmpty(setting)) return null;
        var slash = setting.IndexOf('/');
        if (slash == -1 || setting.IndexOf('/', slash + 1) != -1) return null;
        var light = Js.Trim(setting[..slash]); var dark = Js.Trim(setting[(slash + 1)..]);
        return light.Length == 0 || dark.Length == 0 ? null : (light, dark);
    }

    /// <summary>resolveThemeSetting.</summary>
    public static string? ResolveThemeSetting(string? setting, string terminalTheme)
    {
        if (ParseAutoThemeSetting(setting) is { } auto) return terminalTheme == "light" ? auto.LightTheme : auto.DarkTheme;
        if (setting?.Contains('/') == true) return null;
        return setting;
    }


    /// <summary>initTheme(themeName): the named theme, else (or when it fails to load) the system theme.</summary>
    public void InitTheme(string? themeName)
    {
        var name = themeName ?? SystemThemeName;
        try { _ = LoadTheme(name); CurrentThemeName = name; }
        catch { CurrentThemeName = SystemThemeName; }
    }

    /// <summary>getThemeByName: null when the theme cannot be loaded.</summary>
    public PiTheme? GetThemeByName(string name)
    {
        try { return LoadTheme(name); } catch { return null; }
    }

    /// <summary>loadTheme: the system theme name is reserved; registered, built-in, then custom themes.</summary>
    public PiTheme LoadTheme(string name)
    {
        if (name == SystemThemeName) return CreateSystemTheme();
        if (RegisteredThemePaths.TryGetValue(name, out var registered)) return CreateTheme(ParseThemeJsonContent(registered, File.ReadAllText(registered)), registered);
        return CreateTheme(LoadThemeJson(name), null);
    }

    private PiTheme CreateSystemTheme()
    {
        var generated = SystemTheme.Generate(new(TerminalColors.Foreground, TerminalColors.Background, TerminalColors.Palette,
            TerminalColorsPending ? 0 : 1, GetTerminalTheme()));
        var (fg, bg) = Split(generated.Colors);
        return new(fg, bg, SystemThemeName, null, generated.Appearance, generated.Dim);
    }

    private static readonly HashSet<string> BackgroundTokens = new(StringComparer.Ordinal)
    { "selectedBg", "searchMatchBg", "userMessageBg", "customMessageBg", "toolPendingBg", "toolSuccessBg", "toolErrorBg" };

    private static (List<KeyValuePair<string, object>> Fg, List<KeyValuePair<string, object>> Bg) Split(IEnumerable<KeyValuePair<string, object>> colors)
    {
        var fg = new List<KeyValuePair<string, object>>(); var bg = new List<KeyValuePair<string, object>>();
        foreach (var pair in colors) (BackgroundTokens.Contains(pair.Key) ? bg : fg).Add(pair);
        return (fg, bg);
    }

    private PiTheme CreateTheme(JsObject themeJson, string? sourcePath)
    {
        if (themeJson["colors"] is not JsObject colors) throw new ArgumentException("Invalid theme: colors must be an object.");
        var vars = themeJson["vars"] as JsObject ?? new JsObject();
        // createTheme: withThemeTokenFallbacks(resolveThemeColors(colors, vars)), the missing optional tokens taking their fallback's
        // resolved value in THEME_TOKENS order.
        var resolved = Js.OrderedKeys(colors).Select(key => new KeyValuePair<string, object>(key, ResolveVarRefs(colors[key], vars, []))).ToList();
        foreach (var (token, from) in new[] { ("scrollbarTrack", "muted"), ("scrollbarThumb", "text"), ("searchMatchBg", "selectedBg"),
            ("searchMatchText", "text"), ("thinkingMax", "thinkingXhigh") })
        {
            if (resolved.Any(pair => pair.Key == token)) continue;
            var fallback = resolved.FirstOrDefault(pair => pair.Key == from);
            if (fallback.Key is null) throw new ArgumentException($"Theme token {token} has unresolved fallback {from}");
            resolved.Add(new(token, fallback.Value));
        }
        var (fg, bg) = Split(resolved);
        return new(fg, bg, themeJson["name"] as string, sourcePath, themeJson["appearance"] as string);
    }

    /// <summary>resolveVarRefs: numbers, "", hex and ok*() values are literal; anything else names a variable.</summary>
    private static object ResolveVarRefs(object? value, JsObject vars, HashSet<string> visited)
    {
        if (value is double) return value;
        if (value is not string text) throw new ArgumentException("Invalid color value: " + Js.ToJsString(value));
        if (text.Length == 0 || text.StartsWith('#') || Regex.IsMatch(text, @"^ok(lch|hsl)\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return text;
        if (!visited.Add(text)) throw new ArgumentException($"Circular variable reference detected: {text}");
        if (!vars.Has(text)) throw new ArgumentException($"Variable reference not found: {text}");
        return ResolveVarRefs(vars[text], vars, visited);
    }

    private JsObject LoadThemeJson(string name)
    {
        if (BuiltinThemes.Value.TryGetValue(name, out var builtin)) return (JsObject)Js.ParseJson(StripBom(builtin))!;
        if (RegisteredThemePaths.TryGetValue(name, out var registered)) return ParseThemeJsonContent(registered, File.ReadAllText(registered));
        var themePath = Path.Combine(GetCustomThemesDirectory(), name + ".json");
        if (!File.Exists(themePath)) throw new FileNotFoundException($"Theme not found: {name}");
        return ParseThemeJsonContent(name, File.ReadAllText(themePath));
    }

    private static string StripBom(string content) => content.StartsWith((char)0xFEFF) ? content[1..] : content;

    private JsObject ParseThemeJsonContent(string label, string content)
    {
        object? json;
        try { json = Js.ParseJson(StripBom(content)); }
        catch (FormatException error) { throw new ArgumentException($"Failed to parse theme {label}: SyntaxError: {error.Message}", error); }
        if (ValidateThemeJson) return ThemeJsonValidation.Validate(label, json, StripBom(content));
        if (json is not JsObject obj || !obj.Has("colors")) throw new ArgumentException($"Invalid theme \"{label}\": expected an object with a \"colors\" map.");
        return obj;
    }

    /// <summary>getResolvedThemeColors: every token of the theme as #rrggbb, in Theme.colors order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> GetResolvedThemeColors(string? themeName = null) =>
        LoadTheme(themeName ?? CurrentThemeName ?? SystemThemeName).Colors(this).Select(pair => new KeyValuePair<string, string>(pair.Key, PiColors.ToHex(pair.Value))).ToList();

    /// <summary>isLightTheme.</summary>
    public bool IsLightTheme(string? themeName = null) => LoadTheme(themeName ?? CurrentThemeName ?? SystemThemeName).Appearance(this) == "light";

    /// <summary>getThemeExportColors: explicit export colors from the theme JSON (okhsl() and palette indices as hex).</summary>
    public (string? PageBg, string? CardBg, string? InfoBg) GetThemeExportColors(string? themeName = null)
    {
        var name = themeName ?? CurrentThemeName ?? SystemThemeName;
        if (name == SystemThemeName) return default;
        try
        {
            var themeJson = LoadThemeJson(name);
            if (!Js.Truthy(themeJson["export"])) return default;
            var section = themeJson["export"] as JsObject ?? new JsObject();
            var vars = themeJson["vars"] as JsObject ?? new JsObject();
            string? Resolve(object? value)
            {
                if (value is Js.UndefinedValue) return null;
                var resolved = ResolveVarRefs(value, vars, []);
                if (resolved is double index) return PiColors.ToHex(PiColors.Indexed(index));
                var text = (string)resolved;
                if (text.Length == 0) return null;
                if (Regex.IsMatch(text, @"^okhsl\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return PiColors.ToHex(PiColors.Parse(text));
                return text;
            }
            return (Resolve(section["pageBg"]), Resolve(section["cardBg"]), Resolve(section["infoBg"]));
        }
        catch { return default; }
    }
}

/// <summary>theme-schema.ts validateThemeJson: the published theme schema (strict: unknown top-level fields, color tokens and export
/// colors are rejected), with TypeBox's messages; a name with "/" is refused first.</summary>
internal static class ThemeJsonValidation
{
    internal static JsObject Validate(string label, object? json, string text)
    {
        if (json is JsObject named && named["name"] is string name && name.Contains('/'))
            throw new ArgumentException($"Invalid theme name \"{name}\": theme names cannot contain \"/\" because it is reserved for automatic light/dark theme settings.");
        var schema = PiSharp.CodingAgent.Configuration.PiConfigurationSchemas.Theme;
        if (schema.CheckJson(text)) return (JsObject)json!;
        var missing = new SortedSet<string>(StringComparer.Ordinal); var other = new List<string>();
        foreach (var error in schema.ErrorsJson(text))
        {
            if (error.Keyword == "required" && error.InstancePath == "/colors") { foreach (var color in error.RequiredProperties) missing.Add(color); continue; }
            other.Add($"  - {(error.InstancePath.Length == 0 ? "/" : error.InstancePath)}: {error.Message}");
        }
        var message = new StringBuilder($"Invalid theme \"{label}\":\n");
        if (missing.Count > 0)
        {
            message.Append("\nMissing required color tokens:\n").Append(string.Join("\n", missing.Select(color => "  - " + color)));
            message.Append("\n\nPlease add these colors to your theme's \"colors\" object.");
            message.Append("\nSee the built-in themes (dark.json, light.json) for reference values.");
        }
        if (other.Count > 0) message.Append("\n\nOther errors:\n").Append(string.Join("\n", other));
        throw new ArgumentException(message.ToString());
    }
}
