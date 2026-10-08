// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/export-html/index.ts, with src/config.ts (APP_NAME,
// getExportTemplateDir) and core/agent-session.ts (exportToHtml theme selection).

using System.Text;

namespace PiSharp.CodingAgent.Export;

/// <summary>The agent state the export reads: <c>state.systemPrompt</c> and <c>state.tools</c> (name, description, parameters as JS values).</summary>
public sealed record SessionExportAgentState(string? SystemPrompt, IReadOnlyList<SessionExportTool>? Tools);
/// <summary>One tool of the agent state; <see cref="Js.Undefined"/> members are omitted like JSON.stringify does.</summary>
public sealed record SessionExportTool(string Name, object? Description, object? Parameters)
{
    /// <summary>A declaration as stored JSON ({name, description, parameters}).</summary>
    public static SessionExportTool FromJson(string json)
    {
        var value = Js.ParseJson(json) as JsObject ?? throw new ArgumentException("Tool declaration must be a JSON object.", nameof(json));
        return new(Js.ToJsString(value["name"]), value["description"], value["parameters"]);
    }
    internal JsObject ToJs() => new JsObject().Set("name", Name).Set("description", Description).Set("parameters", Parameters);
}

/// <summary>ExportOptions: output path, theme and the custom tool renderer.</summary>
public sealed record SessionHtmlExportOptions(string? OutputPath = null, string? ThemeName = null, IToolHtmlRenderer? ToolRenderer = null);

/// <summary>
/// Pi's HTML session export: the upstream template, CSS, application script and vendored marked/highlight.js embedded byte-for-byte,
/// theme colors as CSS custom properties, and the session as base64 JSON that the page renders itself.
/// </summary>
public static class SessionHtmlExport
{
    /// <summary>config.ts APP_NAME (package.json piConfig has no name).</summary>
    public const string AppName = "pi";

    /// <summary>Upstream asset blobs at v1.1.0 (git blob SHA-1), embedded byte-for-byte under Export/Assets.</summary>
    public static readonly IReadOnlyDictionary<string, string> AssetBlobs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["template.html"] = "c1d678a0a2fc7df8a37dc952e270dde4b9ecba6a", // packages/coding-agent/src/core/export-html/template.html
        ["template.css"] = "1de5bf46bce9bce1cbbfc6988f75754e74a18721", // packages/coding-agent/src/core/export-html/template.css
        ["template.js"] = "71f121a6687018db62114c9aa3dd713a028630df", // packages/coding-agent/src/core/export-html/template.js
        ["vendor/marked.min.js"] = "9d79575e45d0f6b88158097f5f4622ab938d76d9", // export-html/vendor/marked.min.js (marked v18.0.5, MIT)
        ["vendor/highlight.min.js"] = "5d699ae6a4cfc9544a22a9ac394b01db2e5abd4f", // export-html/vendor/highlight.min.js (highlight.js v11.9.0, BSD-3-Clause)
        ["dark.json"] = "499c10e3c738c3d0a7d4b55af773eeb09e1bd654", // packages/coding-agent/src/modes/interactive/theme/dark.json
        ["light.json"] = "9d29d503d9a07fa34b7197b26c375fb4c628379d" // packages/coding-agent/src/modes/interactive/theme/light.json
    };

    /// <summary>The embedded asset bytes (exactly the upstream blob).</summary>
    public static byte[] ReadAssetBytes(string name)
    {
        var resource = "PiSharp.CodingAgent.Export.Assets." + name.Replace('/', '.');
        using var stream = typeof(SessionHtmlExport).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Missing embedded export asset " + name + ".");
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
    }

    /// <summary>readFileSync(asset, "utf-8"): UTF-8 decoding that keeps a byte order mark as U+FEFF.</summary>
    internal static string ReadAsset(string name) => new UTF8Encoding(false, false).GetString(ReadAssetBytes(name));

    /// <summary>parseColor of index.ts: #RRGGBB or rgb(r, g, b).</summary>
    private static (int R, int G, int B)? ParseCssColor(string color)
    {
        var hex = System.Text.RegularExpressions.Regex.Match(color, "^#([0-9a-fA-F]{2})([0-9a-fA-F]{2})([0-9a-fA-F]{2})\\z");
        if (hex.Success) return (Convert.ToInt32(hex.Groups[1].Value, 16), Convert.ToInt32(hex.Groups[2].Value, 16), Convert.ToInt32(hex.Groups[3].Value, 16));
        var rgb = System.Text.RegularExpressions.Regex.Match(color, @"^rgb[ \t\n\r\f\v]*\([ \t\n\r\f\v]*([0-9]+)[ \t\n\r\f\v]*,[ \t\n\r\f\v]*([0-9]+)[ \t\n\r\f\v]*,[ \t\n\r\f\v]*([0-9]+)[ \t\n\r\f\v]*\)\z");
        if (rgb.Success) return ((int)ParseInt(rgb.Groups[1].Value), (int)ParseInt(rgb.Groups[2].Value), (int)ParseInt(rgb.Groups[3].Value));
        return null;
    }

    private static double ParseInt(string digits) => double.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>getLuminance: relative luminance 0-1.</summary>
    public static double GetLuminance(double r, double g, double b)
    {
        static double ToLinear(double c) { var s = c / 255; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * ToLinear(r) + 0.7152 * ToLinear(g) + 0.0722 * ToLinear(b);
    }

    /// <summary>adjustBrightness: factor &gt; 1 lightens, &lt; 1 darkens; unparseable colors are returned unchanged.</summary>
    public static string AdjustBrightness(string color, double factor)
    {
        if (ParseCssColor(color) is not { } parsed) return color;
        string Adjust(int c) => Js.NumberToString(Math.Min(255, Math.Max(0, Js.Round(c * factor))));
        return $"rgb({Adjust(parsed.R)}, {Adjust(parsed.G)}, {Adjust(parsed.B)})";
    }

    /// <summary>deriveExportColors: page, card and info backgrounds from a base color (userMessageBg).</summary>
    public static (string PageBg, string CardBg, string InfoBg) DeriveExportColors(string baseColor)
    {
        if (ParseCssColor(baseColor) is not { } parsed) return ("rgb(24, 24, 30)", "rgb(30, 30, 36)", "rgb(60, 55, 40)");
        var isLight = GetLuminance(parsed.R, parsed.G, parsed.B) > 0.5;
        if (isLight)
            return (AdjustBrightness(baseColor, 0.96), baseColor, $"rgb({Math.Min(255, parsed.R + 10)}, {Math.Min(255, parsed.G + 5)}, {Math.Max(0, parsed.B - 20)})");
        return (AdjustBrightness(baseColor, 0.7), AdjustBrightness(baseColor, 0.85), $"rgb({Math.Min(255, parsed.R + 20)}, {Math.Min(255, parsed.G + 15)}, {parsed.B})");
    }

    private static string UserMessageBg(IReadOnlyList<KeyValuePair<string, string>> colors)
    {
        var value = colors.LastOrDefault(pair => pair.Key == "userMessageBg").Value;
        return string.IsNullOrEmpty(value) ? "#343541" : value;
    }

    /// <summary>generateThemeVars: one <c>--token: value;</c> per resolved color, then the three export backgrounds.</summary>
    public static string GenerateThemeVars(PiThemeHost themes, string? themeName)
    {
        var colors = themes.GetResolvedThemeColors(themeName);
        var lines = colors.Select(pair => $"--{pair.Key}: {pair.Value};").ToList();
        var themeExport = themes.GetThemeExportColors(themeName);
        var derived = DeriveExportColors(UserMessageBg(colors));
        lines.Add($"--exportPageBg: {themeExport.PageBg ?? derived.PageBg};");
        lines.Add($"--exportCardBg: {themeExport.CardBg ?? derived.CardBg};");
        lines.Add($"--exportInfoBg: {themeExport.InfoBg ?? derived.InfoBg};");
        return string.Join("\n      ", lines);
    }

    /// <summary>generateHtml: fills the template in upstream order with String.prototype.replace semantics.</summary>
    public static string GenerateHtml(JsObject sessionData, PiThemeHost themes, string? themeName)
    {
        ArgumentNullException.ThrowIfNull(sessionData); ArgumentNullException.ThrowIfNull(themes);
        var template = ReadAsset("template.html"); var templateCss = ReadAsset("template.css"); var templateJs = ReadAsset("template.js");
        var markedJs = ReadAsset("vendor/marked.min.js"); var hljsJs = ReadAsset("vendor/highlight.min.js");
        var themeVars = GenerateThemeVars(themes, themeName);
        var colors = themes.GetResolvedThemeColors(themeName);
        var themeExport = themes.GetThemeExportColors(themeName);
        var derived = DeriveExportColors(UserMessageBg(colors));
        var bodyBg = themeExport.PageBg ?? derived.PageBg; var containerBg = themeExport.CardBg ?? derived.CardBg; var infoBg = themeExport.InfoBg ?? derived.InfoBg;
        var sessionDataBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Js.Stringify(sessionData)));
        var css = Js.ReplaceFirst(Js.ReplaceFirst(Js.ReplaceFirst(Js.ReplaceFirst(templateCss, "{{THEME_VARS}}", themeVars), "{{BODY_BG}}", bodyBg),
            "{{CONTAINER_BG}}", containerBg), "{{INFO_BG}}", infoBg);
        var html = Js.ReplaceFirst(template, "{{CSS}}", css);
        html = Js.ReplaceFirst(html, "{{JS}}", templateJs);
        html = Js.ReplaceFirst(html, "{{SESSION_DATA}}", sessionDataBase64);
        html = Js.ReplaceFirst(html, "{{MARKED_JS}}", markedJs);
        return Js.ReplaceFirst(html, "{{HIGHLIGHT_JS}}", hljsJs);
    }

    /// <summary>TEMPLATE_RENDERED_TOOLS: tools the template renders itself.</summary>
    public static readonly IReadOnlySet<string> TemplateRenderedTools = new HashSet<string>(StringComparer.Ordinal) { "bash", "read", "write", "edit", "ls" };

    private static string PropertyKey(object? value) => Js.ToJsString(value);

    /// <summary>preRenderCustomTools: custom tool calls and results rendered by their TUI renderers, keyed by tool call id.</summary>
    public static JsObject PreRenderCustomTools(IEnumerable<object?> entries, IToolHtmlRenderer toolRenderer)
    {
        ArgumentNullException.ThrowIfNull(entries); ArgumentNullException.ThrowIfNull(toolRenderer);
        var rendered = new JsObject();
        foreach (var item in entries)
        {
            if (item is not JsObject entry || entry["type"] is not "message") continue;
            if (entry["message"] is not JsObject message) continue;
            if (message["role"] is "assistant" && message["content"] is List<object?> content)
                foreach (var block in content)
                {
                    if (block is not JsObject toolCall || toolCall["type"] is not "toolCall") continue;
                    var name = toolCall["name"] as string;
                    if (name is not null && TemplateRenderedTools.Contains(name)) continue;
                    var id = PropertyKey(toolCall["id"]);
                    var callHtml = toolRenderer.RenderCall(id, name, toolCall["arguments"]);
                    if (!string.IsNullOrEmpty(callHtml)) rendered[id] = new JsObject().Set("callHtml", callHtml);
                }
            if (message["role"] is "toolResult" && Js.Truthy(message["toolCallId"]))
            {
                var toolCallId = PropertyKey(message["toolCallId"]);
                var toolName = Js.Truthy(message["toolName"]) ? Js.ToJsString(message["toolName"]) : "";
                var existing = rendered.Has(toolCallId) ? rendered[toolCallId] as JsObject : null;
                if (existing is not null || !TemplateRenderedTools.Contains(toolName))
                {
                    var result = toolRenderer.RenderResult(toolCallId, toolName, message["content"], message["details"], Js.Truthy(message["isError"]));
                    if (result is not null)
                    {
                        var merged = existing?.Clone() ?? new JsObject();
                        merged["resultHtmlCollapsed"] = result.Collapsed ?? (object)Js.Undefined;
                        merged["resultHtmlExpanded"] = result.Expanded ?? (object)Js.Undefined;
                        rendered[toolCallId] = merged;
                    }
                }
            }
        }
        return rendered;
    }

    /// <summary>The sessionData object: header, entries, leafId, systemPrompt, tools, renderedTools (undefined members omitted).</summary>
    public static JsObject BuildSessionData(SessionExportSource source, SessionExportAgentState? state, IToolHtmlRenderer? toolRenderer)
    {
        ArgumentNullException.ThrowIfNull(source);
        var entries = source.Entries;
        object? renderedTools = Js.Undefined;
        if (toolRenderer is not null)
        {
            var rendered = PreRenderCustomTools(entries, toolRenderer);
            if (rendered.Count > 0) renderedTools = rendered;
        }
        return new JsObject().Set("header", (object?)source.Header).Set("entries", entries.ToList()).Set("leafId", source.LeafId)
            .Set("systemPrompt", state is null ? Js.Undefined : state.SystemPrompt ?? (object)Js.Undefined)
            .Set("tools", state?.Tools is { } tools ? tools.Select(tool => (object?)tool.ToJs()).ToList() : Js.Undefined)
            .Set("renderedTools", renderedTools);
    }

    /// <summary>exportSessionToHtml's checks: an in-memory session has no file, a lazy one has not written it yet.</summary>
    public static void EnsureExportable(string? sessionFile, Func<string, bool>? exists = null)
    {
        if (sessionFile is null) throw new InvalidOperationException("Cannot export in-memory session to HTML");
        if (!(exists ?? File.Exists)(sessionFile)) throw new InvalidOperationException("Nothing to export yet - start a conversation first");
    }

    /// <summary>The output path as upstream returns it: normalizePath(outputPath) when given (relative stays relative), else
    /// <c>pi-session-&lt;basename&gt;.html</c> in the working directory.</summary>
    public static string OutputPath(string? outputPath, string sessionFile) =>
        !string.IsNullOrEmpty(outputPath) ? ExportPaths.NormalizePath(outputPath) : $"{AppName}-session-{ExportPaths.Basename(sessionFile, ".jsonl")}.html";

    /// <summary>
    /// agent-session exportToHtml theme choice: the first of the requested theme and the settings theme that loads; otherwise the
    /// current theme (initTheme), else the system theme.
    /// </summary>
    public static string? SelectThemeName(PiThemeHost themes, string? requested, string? settingsTheme)
    {
        ArgumentNullException.ThrowIfNull(themes);
        foreach (var candidate in new[] { requested, settingsTheme })
            if (candidate is not null && themes.GetThemeByName(candidate) is not null) return candidate;
        return null;
    }

    /// <summary>exportSessionToHtml: writes the page (UTF-8, no BOM) and returns the output path as given or defaulted.</summary>
    public static async Task<string> ExportSessionToHtmlAsync(SessionExportSource source, SessionExportAgentState? state, SessionHtmlExportOptions? options,
        PiThemeHost themes, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(themes);
        var opts = options ?? new();
        EnsureExportable(source.SessionFile);
        var html = GenerateHtml(BuildSessionData(source, state, opts.ToolRenderer), themes, opts.ThemeName);
        var outputPath = OutputPath(opts.OutputPath, source.SessionFile!);
        await File.WriteAllTextAsync(Rooted(outputPath, workingDirectory), html, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return outputPath;
    }

    /// <summary>
    /// exportFromFile (the <c>--export &lt;file&gt; [output]</c> CLI path): the session file without agent state, rendered with the current
    /// theme (no initTheme runs before it upstream, so the system theme).
    /// </summary>
    public static async Task<string> ExportFromFileAsync(string inputPath, SessionHtmlExportOptions? options = null, PiThemeHost? themes = null,
        string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        var opts = options ?? new(); var host = themes ?? new PiThemeHost();
        var cwd = workingDirectory ?? Environment.CurrentDirectory;
        var resolvedInputPath = ExportPaths.ResolvePath(inputPath, cwd);
        if (!File.Exists(resolvedInputPath)) throw new FileNotFoundException($"File not found: {resolvedInputPath}");
        var source = SessionExportSource.Open(resolvedInputPath, cwd);
        var html = GenerateHtml(BuildSessionData(source, null, null), host, opts.ThemeName);
        var outputPath = !string.IsNullOrEmpty(opts.OutputPath) ? ExportPaths.NormalizePath(opts.OutputPath)
            : $"{AppName}-session-{ExportPaths.Basename(resolvedInputPath, ".jsonl")}.html";
        await File.WriteAllTextAsync(Rooted(outputPath, cwd), html, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return outputPath;
    }

    private static string Rooted(string path, string? workingDirectory) =>
        Path.IsPathRooted(path) || workingDirectory is null ? path : Path.Combine(workingDirectory, path);
}

/// <summary>
/// What a host supplies for agent-session exportToHtml: the theme module state (initTheme, terminal reports, custom themes), the
/// settings theme (settingsManager.getTheme()), the process working directory relative output paths resolve against, and a
/// factory for the custom tool renderer (createToolHtmlRenderer, one per export).
/// </summary>
public sealed record SessionHtmlExportHost(PiThemeHost Themes, string? SettingsTheme = null, string? WorkingDirectory = null,
    Func<IToolHtmlRenderer?>? CreateToolRenderer = null)
{
    /// <summary>A host without a terminal or settings theme: the system theme with ANSI palette indices.</summary>
    public static SessionHtmlExportHost CreateDefault() => new(new PiThemeHost());

    /// <summary>The theme exportToHtml uses for a requested theme (null for none).</summary>
    public string? ResolveThemeName(string? requested = null) => SessionHtmlExport.SelectThemeName(Themes, requested, SettingsTheme);
}

/// <summary>agent-session.ts exportToHtml/exportToJsonl over a persistent session's acknowledged snapshot (the /export and /share seam).</summary>
public static class AgentSessionExport
{
    /// <summary>The session manager view: captured log, selected leaf, and no file for an in-memory session.</summary>
    public static SessionExportSource Source(PersistentAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var snapshot = session.Snapshot;
        return SessionExportSource.FromLog(snapshot.Log.Header, snapshot.Log.Entries, snapshot.Context.LeafId,
            snapshot.Log.StorageDurability == PiSharp.Sessions.Storage.SessionLogStorageDurability.VolatileMemory ? null : session.Path);
    }

    /// <summary>state.systemPrompt and state.tools as the session records them (system prompt and tool declarations).</summary>
    public static SessionExportAgentState State(PersistentAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var system = new PiSharp.Sessions.Context.SessionSystemReplay().Replay(session.Snapshot.Context.Messages);
        return new(system.Prompt, system.Tools.Select(tool => SessionExportTool.FromJson(tool.ToString())).ToList());
    }

    /// <summary>exportToHtml(outputPath, { themeName }): writes the page directly, as upstream does.</summary>
    public static Task<string> ExportToHtmlAsync(PersistentAgentSession session, SessionHtmlExportHost host, string? outputPath = null, string? themeName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        return SessionHtmlExport.ExportSessionToHtmlAsync(Source(session), State(session),
            new(outputPath, host.ResolveThemeName(themeName), host.CreateToolRenderer?.Invoke()), host.Themes, host.WorkingDirectory, cancellationToken);
    }

    /// <summary>exportToJsonl(outputPath): the current branch with a fresh header; returns the resolved path.</summary>
    public static string ExportToJsonl(PersistentAgentSession session, string? outputPath = null, string? workingDirectory = null, Func<DateTimeOffset>? clock = null) =>
        SessionJsonlExport.ExportSessionToJsonl(Source(session), outputPath, null, clock, workingDirectory);

    /// <summary>The /share session for <see cref="SessionShare"/>; HTML is exported with the given theme (theme.name upstream).</summary>
    public static SessionShareSession ShareSession(PersistentAgentSession session, SessionHtmlExportHost host, string? themeName = null,
        IRadiusShareAuthentication? radius = null) =>
        new(() => Source(session), () => State(session), (path, token) => ExportToHtmlAsync(session, host, path, themeName, token), radius);
}
