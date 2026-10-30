using System.Reflection;
using System.Text.RegularExpressions;
using PiSharp.WebAgent.Appearance;
using PiSharp.WebAgent.Shell;

// Decision 0011 (run W3, unit AP): Settings › Appearance. The preference catalogue (AppearancePrefs) is the one appearance.js and
// appearance.css implement (keys, values, defaults, <html> attributes, storage keys, event name); the page renders every control, the theme
// cards, the live preview and the save bar, with no inline script and no number nothing measured; every page's <head> runs appearance.js
// as a classic script right after theme-boot.js and before the stylesheets that read it; the top bar's toggle goes through
// window.PiSharpAppearance, and the later-milestone placeholder is gone.
internal static partial class Program
{
    private static string WebProjectFile(params string[] parts)
    {
        var metadata = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);
        return Path.GetFullPath(Path.Join([metadata["WebProject"]!, .. parts]));
    }

    internal static IEnumerable<(string, Func<Task>)> AppearanceCases() =>
    [
        Case("appearance.catalogue-matches-the-script-and-the-styles", () =>
        {
            var script = File.ReadAllText(WebProjectFile("wwwroot", "js", "appearance.js"));
            var styles = File.ReadAllText(WebProjectFile("wwwroot", "css", "appearance.css"));
            Check(script.Contains($"'{AppearancePrefs.StorageKey}'", StringComparison.Ordinal), "the preferences' storage key");
            Check(script.Contains($"|| '{AppearancePrefs.ThemeKey}'", StringComparison.Ordinal), "the theme's storage key (Web.Common's)");
            Check(script.Contains($"'{AppearancePrefs.ChangeEvent}'", StringComparison.Ordinal), "the change event");
            Check(script.Contains($"'{AppearancePrefs.ThemeAttribute}'", StringComparison.Ordinal), "the theme choice attribute");
            Check(script.Contains("const THEMES = [" + string.Join(", ", AppearancePrefs.Themes.Select(theme => $"'{theme.Value}'")) + "];", StringComparison.Ordinal),
                "the themes");
            Check(script.Contains($"THEMES.includes(value) ? value : '{AppearancePrefs.DefaultTheme}'", StringComparison.Ordinal), "dark is the default theme");

            var parsed = Regex.Matches(script, @"^\s+(\w+): \{ values: ([^,]+(?:, '[^']+')*\]?|SWITCH|\[\.\.\.SWITCH, '[^']+'\]), fallback: '([^']+)', attribute: '([^']+)' \},",
                RegexOptions.Multiline).ToDictionary(match => match.Groups[1].Value, match => match);
            Equal(string.Join(",", AppearancePrefs.All.Select(pref => pref.Key)), string.Join(",", parsed.Keys), "the same preferences, in order");
            foreach (var pref in AppearancePrefs.All)
            {
                var match = parsed[pref.Key];
                var values = match.Groups[2].Value switch
                {
                    "SWITCH" => ["on", "off"],
                    var list when list.StartsWith("[...SWITCH", StringComparison.Ordinal) => ["on", "off", .. Regex.Matches(list, "'([^']+)'").Select(m => m.Groups[1].Value)],
                    var list => Regex.Matches(list, "'([^']+)'").Select(m => m.Groups[1].Value).ToList(),
                };
                var choices = pref.Control == AppearanceControl.Switch ? (pref.Default == "system" ? ["on", "off", "system"] : ["on", "off"]) : pref.Choices.Select(c => c.Value).ToList();
                Equal(string.Join(",", choices.Order()), string.Join(",", values.Order()), pref.Key + " values");
                Equal(pref.Default, match.Groups[3].Value, pref.Key + " default");
                Equal(pref.Attribute, match.Groups[4].Value, pref.Key + " attribute");
                Check(pref.KeyNote == AppearancePrefs.StorageKey + "." + pref.Key + " · this browser", pref.Key + " names where it is kept");
                // Thinking and tool cards open in script; every other preference is a rule of appearance.css for each value but the default.
                if (pref.Key is "expandThinking" or "expandToolCalls")
                {
                    Check(script.Contains($"'{pref.Key}'", StringComparison.Ordinal) || script.Contains(pref.Key + ":", StringComparison.Ordinal), pref.Key + " is applied by the script");
                    continue;
                }
                // Reduce motion "off" (and "system" while the system asks for none) is the page as it is, so it has no rule.
                string[] plain = pref.Key == "reduceMotion" ? ["off", "system"] : [pref.Default];
                foreach (var value in values.Where(value => !plain.Contains(value)))
                    Check(styles.Contains($"[{pref.Attribute}=\"{value}\"]", StringComparison.Ordinal), $"appearance.css styles {pref.Attribute}=\"{value}\"");
            }
            Check(!script.Contains("innerHTML", StringComparison.Ordinal), "appearance.js never writes HTML");
            Check(Regex.IsMatch(script, @"const api = Object\.freeze\(\{") && script.Contains("window.PiSharpAppearance = api", StringComparison.Ordinal), "the API is frozen on window");
            foreach (var member in new[] { "setTheme", "toggleTheme", "save", "prefs", "defaults", "choice", "theme", "persistent" })
                Check(Regex.IsMatch(script, $@"\b{member}\b"), "window.PiSharpAppearance." + member);
        }),

        Case("appearance.shell-toggle-uses-the-appearance-theme", () =>
        {
            var shell = File.ReadAllText(WebProjectFile("wwwroot", "js", "shell.js"));
            Check(shell.Contains("from '/_content/PiSharp.Web.Common/js/theme.js'", StringComparison.Ordinal), "still Web.Common's theme module");
            Check(shell.Contains("export function toggleTheme()", StringComparison.Ordinal), "toggleTheme is exported");
            Check(shell.Contains("window.PiSharpAppearance", StringComparison.Ordinal), "the toggle goes through Settings › Appearance");
            Check(!shell.Contains("initTheme(", StringComparison.Ordinal), "initTheme would replace a System choice and wire a second toggle");
            Equal(1, Regex.Matches(shell, @"closest\('\[data-theme-toggle\]'\)").Count, "one toggle handler");
            Check(shell.Contains("'pisharp:appearance'", StringComparison.Ordinal), "the icons follow a change made elsewhere");
            Check(Regex.IsMatch(shell, @"key === 'l'\) \{[^}]*toggleTheme\(\);"), "Ctrl+Shift+L toggles through it");
            Check(!LaterFeature.SettingsPages.ContainsKey("appearance") && !LaterFeature.SettingsPages.ContainsKey("about"), "no placeholder for Appearance or About");
        }),

        Case("appearance.page-renders-every-control-and-the-preview", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("appearance-page");
            var page = await app.Get("/settings/appearance");
            Equal(200, page.Status, "GET /settings/appearance: " + Cut(page.Text));
            var html = page.Text;
            CheckShell("/settings/appearance", html);
            Check(Regex.IsMatch(html, "<title>Appearance (·|&#xB7;) Settings · PiSharp Web Agent</title>"), "the title");
            Check(!html.Contains("Coming in a later milestone", StringComparison.Ordinal), "not the placeholder");
            Check(html.Contains("<a class=\"active\" aria-current=\"page\" href=\"/settings/appearance\" data-settings-nav=\"appearance\">", StringComparison.Ordinal),
                "Appearance is the active settings item");
            foreach (var theme in AppearancePrefs.Themes)
                Check(html.Contains($"data-theme-set=\"{theme.Value}\"", StringComparison.Ordinal), "the theme card " + theme.Value);
            Check(html.Contains("data-system-now", StringComparison.Ordinal), "System says what the system shows");
            foreach (var pref in AppearancePrefs.All)
            {
                Check(html.Contains($"data-pref-row=\"{pref.Key}\"", StringComparison.Ordinal), "the row " + pref.Key);
                Check(html.Contains(Html(pref.KeyNote), StringComparison.Ordinal), pref.Key + " key note");
                switch (pref.Control)
                {
                    case AppearanceControl.Buttons:
                        foreach (var choice in pref.Choices) Check(html.Contains($"id=\"ap-{pref.Key}-{choice.Value}\"", StringComparison.Ordinal), $"{pref.Key} = {choice.Value}");
                        break;
                    case AppearanceControl.Select:
                        Check(html.Contains($"id=\"ap-{pref.Key}\"", StringComparison.Ordinal), pref.Key + " select");
                        foreach (var choice in pref.Choices) Check(html.Contains($"<option value=\"{choice.Value}\"", StringComparison.Ordinal), $"{pref.Key} option {choice.Value}");
                        break;
                    default:
                        Check(Regex.IsMatch(html, $"name=\"{pref.Key}\"[^>]*role=\"switch\"|role=\"switch\"[^>]*name=\"{pref.Key}\""), pref.Key + " switch");
                        break;
                }
            }
            foreach (var part in new[] { "data-appearance-form", "data-appearance-preview", "data-preview-scale", "data-preview-detail=\"expandThinking\"",
                         "data-preview-detail=\"expandToolCalls\"", "data-savebar", "data-appearance-reset=\"display\"", "data-appearance-reset=\"messages\"",
                         "data-appearance-storage-off", "pisharp-theme · this browser" })
                Check(html.Contains(part, StringComparison.Ordinal), "the page has " + part);
            Check(!html.Contains("Render markdown in user messages", StringComparison.Ordinal), "no option the app cannot honour");
            var usage = Regex.Match(html, "<div class=\"msg-footer pv-usage\">(.*?)</div>", RegexOptions.Singleline);
            Check(usage.Success, "the preview's usage footer");
            Check(!Regex.IsMatch(usage.Groups[1].Value, "[0-9]"), "the sample shows no number nothing measured: " + usage.Groups[1].Value);
            Check(html.Contains("Example text, not a chat of yours", StringComparison.Ordinal), "the preview says it is a sample");
            Equal(200, (await app.Get("/js/appearance.js")).Status, "appearance.js is served");
            Equal(200, (await app.Get("/css/appearance.css")).Status, "appearance.css is served");
        }),

        Case("appearance.every-page-applies-it-before-the-first-paint", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("appearance-head");
            // W3 integration: /setup has its own layout (_SetupLayout), which carries the same Appearance head.
            foreach (var href in new[] { "/chat/new", "/settings/tools", "/settings/about", "/playground", "/setup" })
            {
                var page = await app.Get(href);
                Equal(200, page.Status, href);
                var head = Regex.Match(page.Text, "<head>(.*?)</head>", RegexOptions.Singleline).Groups[1].Value;
                var boot = head.IndexOf("/_content/PiSharp.Web.Common/js/theme-boot.js", StringComparison.Ordinal);
                var appearance = Regex.Match(head, "<script src=\"/js/appearance\\.js[^\"]*\"></script>");
                var styles = head.IndexOf("/css/appearance.css", StringComparison.Ordinal);
                var site = head.IndexOf("/css/site.css", StringComparison.Ordinal);
                Check(boot >= 0 && appearance.Success && styles >= 0 && site >= 0, href + ": theme-boot.js, appearance.js, appearance.css and site.css are in <head>");
                Check(boot < appearance.Index && appearance.Index < styles && styles < site, href + ": theme-boot.js, then appearance.js, then its styles, then site.css");
                Check(!appearance.Value.Contains("module", StringComparison.Ordinal) && !appearance.Value.Contains("defer", StringComparison.Ordinal) &&
                    !appearance.Value.Contains("async", StringComparison.Ordinal), href + ": appearance.js blocks parsing, so it runs before the first paint");
                Check(page.Text.Contains("<html lang=\"en\" data-bs-theme=\"dark\">", StringComparison.Ordinal), href + ": dark until a preference says otherwise");
            }
        }),
    ];
}
