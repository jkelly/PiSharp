// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/interactive/theme/theme.ts (Theme class, theme
// loading, the global theme, the custom theme watcher, getAvailableThemesWithPaths, highlightCode, getLanguageFromPath and the TUI
// theme helpers). Theme JSON loading, validation and the system theme reuse PiThemeHost (src/PiSharp.CodingAgent/Export).
using PiSharp.CodingAgent.Export;
using PiSharp.Tui.Pi;
using TuiColor = PiSharp.Tui.Pi.Color;

namespace PiSharp.Cli.Interactive.Mode;

/// <summary>ThemeStyle: text attributes plus a token (or explicit color) per slot.</summary>
internal sealed record ThemeStyle(string? Fg = null, string? Bg = null, TuiColor? FgColor = null, TuiColor? BgColor = null,
    bool Bold = false, bool Dim = false, bool Italic = false, bool Underline = false, bool Inverse = false, bool Strikethrough = false);

internal sealed class Theme
{
    private static readonly HashSet<string> BackgroundTokens = new(StringComparer.Ordinal)
    { "selectedBg", "searchMatchBg", "userMessageBg", "customMessageBg", "toolPendingBg", "toolSuccessBg", "toolErrorBg" };

    private readonly TerminalColorMode mode;
    private readonly Dictionary<string, string> fgAnsi = new(StringComparer.Ordinal), bgAnsi = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TuiColor> concreteColors = new(StringComparer.Ordinal);
    private readonly List<string> defaultForegroundTokens = [], defaultBackgroundTokens = [];
    private readonly HashSet<string> dimTokens;
    private readonly string? ownAppearance;
    private (TerminalColors Terminal, IReadOnlyDictionary<string, TuiColor> Colors)? resolvedColors;

    public string? Name { get; }
    public string? SourcePath { get; }
    /// <summary>The theme's source info (extension/package themes).</summary>
    public object? SourceInfo { get; set; }

    internal Theme(IEnumerable<(string Token, object Value, bool Background)> tokens, TerminalColorMode mode, string? name, string? sourcePath,
        string? appearance, IEnumerable<string>? dim)
    {
        Name = name; SourcePath = sourcePath; this.mode = mode;
        dimTokens = new(dim ?? [], StringComparer.Ordinal);
        foreach (var (token, value, background) in tokens)
        {
            var isBackground = background || BackgroundTokens.Contains(token);
            if (value is "")
            {
                (isBackground ? defaultBackgroundTokens : defaultForegroundTokens).Add(token);
                (isBackground ? bgAnsi : fgAnsi)[token] = isBackground ? "\u001b[49m" : "\u001b[39m";
                continue;
            }
            var color = value switch
            {
                double index => Colors.ParseColor((int)index),
                int index => Colors.ParseColor(index),
                string text => Colors.ParseColor(text),
                _ => throw new ArgumentException($"Invalid color value: {value}")
            };
            concreteColors[token] = color;
            (isBackground ? bgAnsi : fgAnsi)[token] = isBackground ? Colors.BackgroundAnsi(color, mode) : Colors.ForegroundAnsi(color, mode);
        }
        ownAppearance = appearance;
    }

    /// <summary>The background the theme is designed for, or the terminal's appearance.</summary>
    public string Appearance => ownAppearance ?? Themes.GetTerminalTheme();

    /// <summary>Concrete colors for all tokens; "" tokens use the terminal's reported defaults or a guess.</summary>
    public IReadOnlyDictionary<string, TuiColor> TokenColors
    {
        get
        {
            var terminal = Themes.TerminalColorsReport;
            if (resolvedColors is { } cached && ReferenceEquals(cached.Terminal, terminal)) return cached.Colors;
            var dark = Appearance == "dark";
            var foreground = terminal.Foreground is { } f ? TuiColor.Rgb(f.R, f.G, f.B) : Colors.ParseColor(dark ? "#e5e5e7" : "#000000");
            var background = terminal.Background is { } b ? TuiColor.Rgb(b.R, b.G, b.B) : Colors.ParseColor(dark ? "#000000" : "#ffffff");
            var colors = new Dictionary<string, TuiColor>(concreteColors, StringComparer.Ordinal);
            foreach (var token in defaultForegroundTokens) colors[token] = foreground;
            foreach (var token in defaultBackgroundTokens) colors[token] = background;
            foreach (var token in dimTokens) if (colors.TryGetValue(token, out var color)) colors[token] = Colors.Mix(color, background, 0.4);
            resolvedColors = (terminal, colors);
            return colors;
        }
    }

    public string Style(string text, ThemeStyle options)
    {
        var dim = options.Dim || options.Fg is { } token && dimTokens.Contains(token);
        var fg = options.Fg is { } fgToken ? TokenAnsi(fgAnsi, fgToken) : options.FgColor is { } fgColor ? Colors.ForegroundAnsi(fgColor, mode) : null;
        var bg = options.Bg is { } bgToken ? TokenAnsi(bgAnsi, bgToken) : options.BgColor is { } bgColor ? Colors.BackgroundAnsi(bgColor, mode) : null;
        return Colors.StyleTextWithAnsi(text, fg, bg, new TextStyle(null, null, options.Bold, dim, options.Italic, options.Underline, options.Inverse, options.Strikethrough));
    }

    public string Fg(string color, string text)
    {
        var ansi = TokenAnsi(fgAnsi, color);
        if (dimTokens.Contains(color)) return ansi + "\u001b[2m" + text + "\u001b[22;39m";
        return ansi + text + "\u001b[39m";
    }

    public string Bg(string color, string text) => TokenAnsi(bgAnsi, color) + text + "\u001b[49m";

    private static string TokenAnsi(Dictionary<string, string> ansi, string token) =>
        ansi.TryGetValue(token, out var value) ? value : throw new ArgumentException($"Unknown theme color: {token}");

    public string Bold(string text) => "\u001b[1m" + text + "\u001b[22m";
    public string Italic(string text) => "\u001b[3m" + text + "\u001b[23m";
    public string Underline(string text) => "\u001b[4m" + text + "\u001b[24m";
    public string Inverse(string text) => "\u001b[7m" + text + "\u001b[27m";
    public string Strikethrough(string text) => "\u001b[9m" + text + "\u001b[29m";

    public string GetFgAnsi(string color)
    {
        var ansi = TokenAnsi(fgAnsi, color);
        return dimTokens.Contains(color) ? ansi + "\u001b[2m" : ansi;
    }
    public string GetBgAnsi(string color) => TokenAnsi(bgAnsi, color);
    public TerminalColorMode GetColorMode() => mode;

    public Func<string, string> GetThinkingBorderColor(string level) => level switch
    {
        "minimal" => text => Fg("thinkingMinimal", text),
        "low" => text => Fg("thinkingLow", text),
        "medium" => text => Fg("thinkingMedium", text),
        "high" => text => Fg("thinkingHigh", text),
        "xhigh" => text => Fg("thinkingXhigh", text),
        "max" => text => Fg("thinkingMax", text),
        _ => text => Fg("thinkingOff", text)
    };

    public Func<string, string> GetBashModeBorderColor() => text => Fg("bashMode", text);

    internal static Theme FromPiTheme(PiTheme source, TerminalColorMode mode, string? sourcePath = null) =>
        new(source.RawTokens, mode, source.Name, sourcePath ?? source.SourcePath, source.OwnAppearance, source.DimTokens);
}

internal sealed record ThemeInfo(string Name, string? Path);

/// <summary>The theme.ts module state: the global theme, terminal reports, registered themes and the custom theme watcher.</summary>
internal static class Themes
{
    public const string SystemThemeName = PiThemeHost.SystemThemeName;
    private static readonly object Gate = new();
    private static Theme? current;
    private static string? currentThemeName;
    private static readonly Dictionary<string, Theme> RegisteredThemes = new(StringComparer.Ordinal);
    private static FileSystemWatcher? watcher;
    private static Timer? reloadTimer;
    private static Action? onThemeChange;

    /// <summary>getAgentDir override (tests); null derives it from PI_CODING_AGENT_DIR.</summary>
    public static string? AgentDirectory { get; set; }
    public static Func<string, string?> Environment { get; set; } = System.Environment.GetEnvironmentVariable;
    /// <summary>The terminal's reported colors; replaced, never mutated.</summary>
    public static TerminalColors TerminalColorsReport { get; private set; } = new(null, null, null);
    public static bool TerminalColorsPending { get; private set; }
    public static string? TerminalColorScheme { get; private set; }
    /// <summary>Forced color mode (tests); null uses the terminal capabilities.</summary>
    public static TerminalColorMode? ColorModeOverride { get; set; }

    public static Theme Current => current ?? throw new InvalidOperationException("Theme not initialized. Call initTheme() first.");
    public static bool IsInitialized => current is not null;
    public static string? CurrentThemeName => currentThemeName;

    public static void SetTerminalColors(TerminalColors colors) { TerminalColorsReport = colors with { }; TerminalColorsPending = false; }
    public static void SetTerminalColorScheme(TerminalColorScheme? scheme) => TerminalColorScheme = scheme switch { PiSharp.Tui.Pi.TerminalColorScheme.Light => "light", PiSharp.Tui.Pi.TerminalColorScheme.Dark => "dark", _ => null };
    public static void MarkTerminalColorsPending() => TerminalColorsPending = true;

    private static RgbChannels? Channels(RgbColor? color) => color is { } c ? new RgbChannels(c.R, c.G, c.B) : null;

    private static PiThemeHost Host(IReadOnlyDictionary<string, string>? registered = null) => new()
    {
        Environment = Environment, AgentDirectory = AgentDirectory,
        RegisteredThemePaths = registered ?? new Dictionary<string, string>(StringComparer.Ordinal),
        TerminalColors = new TerminalColorReport(Channels(TerminalColorsReport.Foreground), Channels(TerminalColorsReport.Background),
            TerminalColorsReport.Palette?.Select(color => new RgbChannels(color.R, color.G, color.B)).ToList()),
        TerminalColorScheme = TerminalColorScheme, TerminalColorsPending = TerminalColorsPending, ValidateThemeJson = true
    };

    private static TerminalColorMode Mode => ColorModeOverride ?? Colors.GetTerminalColorMode();

    public static string GetCustomThemesDir() => Host().GetCustomThemesDirectory();

    /// <summary>getTerminalTheme: the reported background, then the light/dark report, then COLORFGBG, then dark.</summary>
    public static string GetTerminalTheme() => Host().GetTerminalTheme();

    public static (string LightTheme, string DarkTheme)? ParseAutoThemeSetting(string? setting) => PiThemeHost.ParseAutoThemeSetting(setting);
    public static string? ResolveThemeSetting(string? setting, string terminalTheme) => PiThemeHost.ResolveThemeSetting(setting, terminalTheme);

    public static Theme LoadThemeFromPath(string themePath)
    {
        const string key = "\u0000path";
        var loaded = Host(new Dictionary<string, string>(StringComparer.Ordinal) { [key] = themePath }).LoadTheme(key);
        return Theme.FromPiTheme(loaded, Mode, themePath);
    }

    private static Theme LoadTheme(string name)
    {
        if (name == SystemThemeName) return Theme.FromPiTheme(Host().LoadTheme(SystemThemeName), Mode);
        lock (Gate) if (RegisteredThemes.TryGetValue(name, out var registered)) return registered;
        return Theme.FromPiTheme(Host().LoadTheme(name), Mode);
    }

    public static Theme? GetThemeByName(string name)
    {
        try { return LoadTheme(name); } catch { return null; }
    }

    public static void SetRegisteredThemes(IEnumerable<Theme> themes)
    {
        lock (Gate)
        {
            RegisteredThemes.Clear();
            foreach (var theme in themes)
            {
                if (theme.Name is not { } name) continue;
                if (name.Contains('/'))
                    throw new ArgumentException($"Invalid theme name \"{name}\": theme names cannot contain \"/\" because it is reserved for automatic light/dark theme settings.");
                RegisteredThemes[name] = theme;
            }
        }
    }

    /// <summary>setRegisteredThemes from theme files (name, path); files that fail to load are skipped (the resource loader reports them).</summary>
    public static void SetRegisteredThemes(IEnumerable<(string Name, string Path)> themes)
    {
        var loaded = new List<Theme>();
        foreach (var (_, path) in themes)
        {
            try { loaded.Add(LoadThemeFromPath(path)); } catch { }
        }
        SetRegisteredThemes(loaded);
    }

    public static void SetRegisteredThemes(IEnumerable<PiTheme> themes) => SetRegisteredThemes(themes.Select(theme => Theme.FromPiTheme(theme, Mode)));

    public static IReadOnlyList<string> GetAvailableThemes() => GetAvailableThemesWithPaths().Select(info => info.Name).ToList();

    public static IReadOnlyList<ThemeInfo> GetAvailableThemesWithPaths()
    {
        var result = new List<ThemeInfo>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(ThemeInfo info) { if (seen.Add(info.Name)) result.Add(info); }
        Add(new(SystemThemeName, null));
        Add(new("dark", null)); Add(new("light", null));
        var customDir = GetCustomThemesDir();
        if (Directory.Exists(customDir))
            foreach (var file in Directory.EnumerateFiles(customDir).Where(file => file.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                try { if (LoadThemeFromPath(file).Name is { } name) Add(new(name, file)); }
                catch { /* Invalid themes are reported by the resource loader. */ }
            }
        lock (Gate) foreach (var (name, theme) in RegisteredThemes) Add(new(name, theme.SourcePath));
        return result.OrderBy(info => info.Name == SystemThemeName ? 0 : 1).ThenBy(info => info.Name, StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture, false)).ToList();
    }

    public static void InitTheme(string? themeName = null, bool enableWatcher = false)
    {
        var name = themeName ?? SystemThemeName;
        currentThemeName = name;
        try
        {
            current = LoadTheme(name);
            if (enableWatcher) StartThemeWatcher();
        }
        catch
        {
            currentThemeName = SystemThemeName;
            current = LoadTheme(SystemThemeName);
        }
    }

    public static (bool Success, string? Error) SetTheme(string name, bool enableWatcher = false)
    {
        currentThemeName = name;
        try
        {
            current = LoadTheme(name);
            if (enableWatcher) StartThemeWatcher();
            onThemeChange?.Invoke();
            return (true, null);
        }
        catch (Exception error)
        {
            currentThemeName = SystemThemeName;
            current = LoadTheme(SystemThemeName);
            return (false, error.Message);
        }
    }

    public static void SetThemeInstance(Theme theme)
    {
        current = theme; currentThemeName = "<in-memory>";
        StopThemeWatcher();
        onThemeChange?.Invoke();
    }

    /// <summary>Re-create the current theme (terminal colors or color mode changed); keeps the name.</summary>
    public static void RefreshCurrentTheme()
    {
        if (currentThemeName is null or "<in-memory>") return;
        try { current = LoadTheme(currentThemeName); } catch { }
    }

    public static void OnThemeChange(Action? callback) => onThemeChange = callback;

    private static void StartThemeWatcher()
    {
        StopThemeWatcher();
        if (currentThemeName is null or "dark" or "light" or SystemThemeName) return;
        var customDir = GetCustomThemesDir();
        var watchedName = currentThemeName;
        var watchedFile = watchedName + ".json";
        var themeFile = Path.Join(customDir, watchedFile);
        if (!File.Exists(themeFile)) return;
        void ScheduleReload()
        {
            lock (Gate)
            {
                reloadTimer?.Dispose();
                reloadTimer = new Timer(_ =>
                {
                    lock (Gate) { reloadTimer?.Dispose(); reloadTimer = null; }
                    if (currentThemeName != watchedName || !File.Exists(themeFile)) return;
                    try
                    {
                        var reloaded = LoadThemeFromPath(themeFile);
                        lock (Gate) RegisteredThemes[watchedName] = reloaded;
                        current = reloaded;
                        onThemeChange?.Invoke();
                    }
                    catch { /* The file may be mid-edit. */ }
                }, null, 100, Timeout.Infinite);
            }
        }
        try
        {
            var created = new FileSystemWatcher(customDir) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            FileSystemEventHandler handler = (_, change) =>
            {
                if (currentThemeName != watchedName) return;
                if (string.IsNullOrEmpty(change.Name) || change.Name == watchedFile) ScheduleReload();
            };
            created.Changed += handler; created.Created += handler; created.Deleted += handler;
            created.Renamed += (_, change) => { if (change.Name == watchedFile || change.OldName == watchedFile) ScheduleReload(); };
            created.Error += (_, _) => StopThemeWatcher();
            created.EnableRaisingEvents = true;
            watcher = created;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException) { }
    }

    public static void StopThemeWatcher()
    {
        lock (Gate) { reloadTimer?.Dispose(); reloadTimer = null; }
        var previous = watcher; watcher = null;
        try { previous?.Dispose(); } catch { }
    }

    /// <summary>isLightTheme.</summary>
    public static bool IsLightTheme(string? themeName = null) => LoadTheme(themeName ?? currentThemeName ?? SystemThemeName).Appearance == "light";

    // ---------------------------------------------------------------- TUI helpers
    public static IReadOnlyList<string> HighlightCode(string code, string? lang)
    {
        var theme = Current;
        var validLang = lang is { Length: > 0 } && SyntaxHighlight.SupportsLanguage(lang) ? lang : null;
        if (validLang is null) return code.Split('\n').Select(line => theme.Fg("mdCodeBlock", line)).ToList();
        try { return SyntaxHighlight.Highlight(code, validLang, theme).Split('\n'); }
        catch { return code.Split('\n'); }
    }

    private static readonly Dictionary<string, string> ExtensionLanguages = new(StringComparer.Ordinal)
    {
        ["ts"] = "typescript", ["tsx"] = "typescript", ["js"] = "javascript", ["jsx"] = "javascript", ["mjs"] = "javascript", ["cjs"] = "javascript",
        ["py"] = "python", ["rb"] = "ruby", ["rs"] = "rust", ["go"] = "go", ["java"] = "java", ["kt"] = "kotlin", ["swift"] = "swift",
        ["c"] = "c", ["h"] = "c", ["cpp"] = "cpp", ["cc"] = "cpp", ["cxx"] = "cpp", ["hpp"] = "cpp", ["cs"] = "csharp", ["php"] = "php",
        ["sh"] = "bash", ["bash"] = "bash", ["zsh"] = "bash", ["fish"] = "fish", ["ps1"] = "powershell", ["sql"] = "sql", ["html"] = "html",
        ["htm"] = "html", ["css"] = "css", ["scss"] = "scss", ["sass"] = "sass", ["less"] = "less", ["json"] = "json", ["yaml"] = "yaml",
        ["yml"] = "yaml", ["toml"] = "toml", ["xml"] = "xml", ["md"] = "markdown", ["markdown"] = "markdown", ["dockerfile"] = "dockerfile",
        ["makefile"] = "makefile", ["cmake"] = "cmake", ["lua"] = "lua", ["perl"] = "perl", ["r"] = "r", ["scala"] = "scala", ["clj"] = "clojure",
        ["ex"] = "elixir", ["exs"] = "elixir", ["erl"] = "erlang", ["hs"] = "haskell", ["ml"] = "ocaml", ["vim"] = "vim", ["graphql"] = "graphql",
        ["proto"] = "protobuf", ["tf"] = "hcl", ["hcl"] = "hcl"
    };

    public static string? GetLanguageFromPath(string filePath)
    {
        var ext = filePath.Split('.')[^1].ToLowerInvariant();
        if (ext.Length == 0) return null;
        return ExtensionLanguages.GetValueOrDefault(ext);
    }

    public static MarkdownTheme GetMarkdownTheme(string codeBlockIndent = "  ") => new()
    {
        Heading = text => Current.Fg("mdHeading", text),
        Link = text => Current.Fg("mdLink", text),
        LinkUrl = text => Current.Fg("mdLinkUrl", text),
        Code = text => Current.Fg("mdCode", text),
        CodeBlock = text => Current.Fg("mdCodeBlock", text),
        CodeBlockBorder = text => Current.Fg("mdCodeBlockBorder", text),
        Quote = text => Current.Fg("mdQuote", text),
        QuoteBorder = text => Current.Fg("mdQuoteBorder", text),
        Hr = text => Current.Fg("mdHr", text),
        ListBullet = text => Current.Fg("mdListBullet", text),
        Bold = text => Current.Bold(text),
        Italic = text => Current.Italic(text),
        Underline = text => Current.Underline(text),
        Strikethrough = text => Current.Strikethrough(text),
        HighlightCode = (code, lang) =>
        {
            var validLang = lang is { Length: > 0 } && SyntaxHighlight.SupportsLanguage(lang) ? lang : null;
            if (validLang is null) return code.Split('\n').Select(line => Current.Fg("mdCodeBlock", line)).ToList();
            try { return [.. SyntaxHighlight.Highlight(code, validLang, Current).Split('\n')]; }
            catch { return code.Split('\n').Select(line => Current.Fg("mdCodeBlock", line)).ToList(); }
        },
        CodeBlockIndent = codeBlockIndent
    };

    public static SelectListTheme GetSelectListTheme() => new(
        text => Current.Fg("accent", text), text => Current.Fg("accent", text), text => Current.Fg("muted", text),
        text => Current.Fg("muted", text), text => Current.Fg("muted", text));

    public static EditorTheme GetEditorTheme() => new(text => Current.Fg("borderMuted", text), GetSelectListTheme());

    public static SettingsListTheme GetSettingsListTheme() => new(
        (text, selected) => selected ? Current.Fg("accent", text) : text,
        (text, selected) => selected ? Current.Fg("accent", text) : Current.Fg("muted", text),
        text => Current.Fg("dim", text), Current.Fg("accent", "→ "), text => Current.Fg("dim", text));

    /// <summary>Test hook: reset module state.</summary>
    internal static void ResetForTests()
    {
        StopThemeWatcher(); current = null; currentThemeName = null; onThemeChange = null; AgentDirectory = null;
        lock (Gate) RegisteredThemes.Clear();
        TerminalColorsReport = new(null, null, null); TerminalColorsPending = false; TerminalColorScheme = null;
    }
}

/// <summary>The global <c>theme</c> of theme.ts for <c>using static</c>.</summary>
internal static class ThemeGlobals
{
#pragma warning disable IDE1006
    public static Theme theme => Themes.Current;
#pragma warning restore IDE1006
}
