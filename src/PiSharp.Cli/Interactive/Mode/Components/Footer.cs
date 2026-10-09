// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/footer.ts.
// Also ports UsageTotals, createUsageTotals and addUsageToTotals (core/usage-totals.ts) and areExperimentalFeaturesEnabled
// (core/experimental.ts).
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Source ContextUsage (extensions/types.ts): <c>Percent</c> null means unknown (after compaction, until the next response).</summary>
internal sealed record FooterContextUsage(double? Tokens, double ContextWindow, double? Percent);

/// <summary>Source AgentSession.routedModel: the physical model a virtual model routed the latest request to.</summary>
internal sealed record FooterRoutedModel(JsonObject Model, string? ThinkingLevel = null);

/// <summary>
/// What footer.ts reads from AgentSession: <c>state.model</c>, <c>state.thinkingLevel</c>, <c>model</c>, <c>routedModel</c>,
/// <c>getContextUsage()</c>, <c>modelRuntime.isUsingSubscription(provider)</c> and from <c>sessionManager</c>: entry count, session id,
/// leaf id, entries, cwd and session name. Models and entries are Pi JSON (model: id, provider, contextWindow, reasoning; entries:
/// SessionEntry with usage { input, output, cacheRead, cacheWrite, cost: { total } }).
/// </summary>
internal interface IFooterSession
{
    /// <summary>session.state.model.</summary>
    JsonObject? StateModel { get; }
    /// <summary>session.state.thinkingLevel.</summary>
    string? StateThinkingLevel { get; }
    /// <summary>session.model, the model whose context window applies when no route is active (compared by reference for caching).</summary>
    JsonObject? Model { get; }
    /// <summary>session.routedModel.</summary>
    FooterRoutedModel? RoutedModel { get; }
    /// <summary>session.getContextUsage().</summary>
    FooterContextUsage? GetContextUsage();
    /// <summary>session.modelRuntime.isUsingSubscription(provider).</summary>
    bool IsUsingSubscription(string provider);
    /// <summary>sessionManager.getEntryCount().</summary>
    int GetEntryCount();
    /// <summary>sessionManager.getSessionId().</summary>
    string GetSessionId();
    /// <summary>sessionManager.getLeafId().</summary>
    string? GetLeafId();
    /// <summary>sessionManager.getEntries().</summary>
    IReadOnlyList<JsonObject> GetEntries();
    /// <summary>sessionManager.getCwd().</summary>
    string GetCwd();
    /// <summary>sessionManager.getSessionName().</summary>
    string? GetSessionName();
}

/// <summary>Source UsageTotals.</summary>
internal sealed class UsageTotals
{
    public double Input { get; set; }
    public double Output { get; set; }
    public double CacheRead { get; set; }
    public double CacheWrite { get; set; }
    public double Cost { get; set; }

    /// <summary>Source addUsageToTotals over a Pi Usage JSON object.</summary>
    public void Add(JsonObject usage)
    {
        Input += Num(usage["input"]);
        Output += Num(usage["output"]);
        CacheRead += Num(usage["cacheRead"]);
        CacheWrite += Num(usage["cacheWrite"]);
        Cost += Num((usage["cost"] as JsonObject)?["total"]);
    }

    internal static double Num(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;
}

/// <summary>
/// Footer component that shows pwd, token stats, and context usage.
/// Computes token/context stats from session, gets git branch and extension statuses from provider.
/// </summary>
internal sealed partial class FooterComponent : IComponent
{
    private bool autoCompactEnabled = true;
    private IFooterSession session;
    private readonly IReadonlyFooterDataProvider footerData;
    private SessionStats? sessionStats;

    private sealed record SessionStats(IFooterSession Session, string SessionId, string? LeafId, int EntryCount, JsonObject? LimitsModel,
        UsageTotals UsageTotals, double? LatestCacheHitRate, FooterContextUsage? ContextUsage);

    public FooterComponent(IFooterSession session, IReadonlyFooterDataProvider footerData)
    {
        this.session = session;
        this.footerData = footerData;
    }

    /// <summary>process.env (HOME, USERPROFILE, PI_EXPERIMENTAL).</summary>
    public Func<string, string?> Environment { get; set; } = System.Environment.GetEnvironmentVariable;

    public void SetSession(IFooterSession session) => this.session = session;

    public void SetAutoCompactEnabled(bool enabled) => autoCompactEnabled = enabled;

    /// <summary>
    /// No-op: git branch caching now handled by provider.
    /// Kept for compatibility with existing call sites in interactive-mode.
    /// </summary>
    public void Invalidate() { }

    /// <summary>Clean up resources. Git watcher cleanup now handled by provider.</summary>
    public void Dispose() { }

    [GeneratedRegex("[\r\n\t]")] private static partial Regex ControlWhitespace();
    [GeneratedRegex(" +")] private static partial Regex Spaces();

    /// <summary>
    /// Sanitize text for display in a single-line status.
    /// Removes newlines, tabs, carriage returns, and other control characters.
    /// </summary>
    private static string SanitizeStatusText(string text) => TextUtils.JsTrim(Spaces().Replace(ControlWhitespace().Replace(text, " "), " "));

    private static string Fixed(double value, int digits) => value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    private static double JsRound(double value) => Math.Floor(value + 0.5);

    /// <summary>Format token counts for compact footer display.</summary>
    internal static string FormatTokens(double count)
    {
        if (count < 1000) return count.ToString(CultureInfo.InvariantCulture);
        if (count < 10000) return $"{Fixed(count / 1000, 1)}k";
        if (count < 1000000) return $"{JsRound(count / 1000).ToString(CultureInfo.InvariantCulture)}k";
        if (count < 10000000) return $"{Fixed(count / 1000000, 1)}M";
        return $"{JsRound(count / 1000000).ToString(CultureInfo.InvariantCulture)}M";
    }

    internal static string FormatCwdForFooter(string cwd, string? home)
    {
        if (string.IsNullOrEmpty(home)) return cwd;

        var resolvedCwd = Path.GetFullPath(cwd);
        var resolvedHome = Path.GetFullPath(home);
        var relativeToHome = Path.GetRelativePath(resolvedHome, resolvedCwd);
        if (relativeToHome == ".") relativeToHome = "";
        var sep = Path.DirectorySeparatorChar;
        var isInsideHome = relativeToHome.Length == 0 ||
            (relativeToHome != ".." && !relativeToHome.StartsWith($"..{sep}", StringComparison.Ordinal) && !Path.IsPathRooted(relativeToHome));

        if (!isInsideHome) return cwd;
        return relativeToHome.Length == 0 ? "~" : $"~{sep}{relativeToHome}";
    }

    private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static bool Truthy(JsonNode? node) => node is JsonValue value && (value.TryGetValue<bool>(out var flag) ? flag : true);

    /// <summary>
    /// Usage totals and context usage scan the whole session, and the footer renders on every frame.
    /// Entries are append-only and every append moves the leaf, so the results only change with the
    /// session, leaf, entry count, or the model whose context window applies.
    /// </summary>
    private SessionStats GetSessionStats()
    {
        var entryCount = session.GetEntryCount();
        var sessionId = session.GetSessionId();
        var leafId = session.GetLeafId();
        var limitsModel = session.RoutedModel?.Model ?? session.Model;
        var cached = sessionStats;
        if (cached is not null && ReferenceEquals(cached.Session, session) && cached.SessionId == sessionId && cached.LeafId == leafId &&
            cached.EntryCount == entryCount && ReferenceEquals(cached.LimitsModel, limitsModel))
            return cached;

        // Calculate cumulative usage from ALL session entries (not just post-compaction messages)
        var usageTotals = new UsageTotals();
        double? latestCacheHitRate = null;

        foreach (var entry in session.GetEntries())
        {
            var type = Str(entry["type"]);
            var message = entry["message"] as JsonObject;
            var role = Str(message?["role"]);
            if (type == "usage")
            {
                if (entry["usage"] is JsonObject usage) usageTotals.Add(usage);
            }
            else if (type == "message" && role == "assistant")
            {
                if (message!["usage"] is not JsonObject usage) continue;
                usageTotals.Add(usage);

                var latestPromptTokens = UsageTotals.Num(usage["input"]) + UsageTotals.Num(usage["cacheRead"]) + UsageTotals.Num(usage["cacheWrite"]);
                latestCacheHitRate = latestPromptTokens > 0 ? UsageTotals.Num(usage["cacheRead"]) / latestPromptTokens * 100 : null;
            }
            else if (type == "message" && role == "toolResult" && message!["usage"] is JsonObject toolUsage)
            {
                usageTotals.Add(toolUsage);
            }
            else if (type is "branch_summary" or "compaction" && entry["usage"] is JsonObject summaryUsage)
            {
                usageTotals.Add(summaryUsage);
            }
        }

        // Calculate context usage from session (handles compaction correctly).
        // After compaction, tokens are unknown until the next LLM response.
        var contextUsage = session.GetContextUsage();
        sessionStats = new(session, sessionId, leafId, entryCount, limitsModel, usageTotals, latestCacheHitRate, contextUsage);
        return sessionStats;
    }

    public List<string> Render(int width)
    {
        var stateModel = session.StateModel;
        var (_, _, _, _, _, usageTotals, latestCacheHitRate, contextUsage) = GetSessionStats();
        var contextWindow = contextUsage?.ContextWindow ?? (stateModel?["contextWindow"] is { } window ? UsageTotals.Num(window) : 0);
        var contextPercentValue = contextUsage?.Percent ?? 0;
        // `contextUsage?.percent !== null`: an absent context usage still shows 0.0.
        var contextPercent = contextUsage is null || contextUsage.Percent is not null ? Fixed(contextPercentValue, 1) : "?";

        // Replace home directory with ~
        var home = Environment("HOME") is { Length: > 0 } homeDir ? homeDir : Environment("USERPROFILE");
        var pwd = FormatCwdForFooter(session.GetCwd(), home);

        // Add git branch if available
        var branch = footerData.GetGitBranch();
        if (!string.IsNullOrEmpty(branch)) pwd = $"{pwd} ({branch})";

        // Add session name if set
        var sessionName = session.GetSessionName();
        if (!string.IsNullOrEmpty(sessionName)) pwd = $"{pwd} • {sessionName}";

        // Build stats line
        var statsParts = new List<string>();
        if (usageTotals.Input != 0) statsParts.Add($"↑{FormatTokens(usageTotals.Input)}");
        if (usageTotals.Output != 0) statsParts.Add($"↓{FormatTokens(usageTotals.Output)}");
        if (usageTotals.CacheRead != 0) statsParts.Add($"R{FormatTokens(usageTotals.CacheRead)}");
        if (usageTotals.CacheWrite != 0) statsParts.Add($"W{FormatTokens(usageTotals.CacheWrite)}");
        if ((usageTotals.CacheRead > 0 || usageTotals.CacheWrite > 0) && latestCacheHitRate is { } hitRate)
            statsParts.Add($"CH{Fixed(hitRate, 1)}%");

        // Kimi Coding is subscription-backed despite using API-key authentication.
        var provider = Str(stateModel?["provider"]) ?? "";
        var usingSubscription = stateModel is not null && (provider == "kimi-coding" || session.IsUsingSubscription(provider));
        if (usageTotals.Cost != 0 || usingSubscription)
        {
            var costStr = $"${Fixed(usageTotals.Cost, 3)}{(usingSubscription ? " (sub)" : "")}";
            statsParts.Add(costStr);
        }

        // Colorize context percentage based on usage
        string contextPercentStr;
        var autoIndicator = autoCompactEnabled ? " (auto)" : "";
        var contextPercentDisplay = contextPercent == "?"
            ? $"?/{FormatTokens(contextWindow)}{autoIndicator}"
            : $"{contextPercent}%/{FormatTokens(contextWindow)}{autoIndicator}";
        if (contextPercentValue > 90) contextPercentStr = theme.Fg("error", contextPercentDisplay);
        else if (contextPercentValue > 70) contextPercentStr = theme.Fg("warning", contextPercentDisplay);
        else contextPercentStr = contextPercentDisplay;
        statsParts.Add(contextPercentStr);
        if (Environment("PI_EXPERIMENTAL") == "1")
            statsParts.Add($"{theme.Fg("dim", "•")} {theme.Bold(theme.Fg("warning", "xp"))}");

        var statsLeft = string.Join(" ", statsParts);

        // Add model name on the right side, plus thinking level if model supports it
        var modelName = Str(stateModel?["id"]) is { Length: > 0 } id ? id : "no-model";

        var statsLeftWidth = TextUtils.VisibleWidth(statsLeft);

        // If statsLeft is too wide, truncate it
        if (statsLeftWidth > width)
        {
            statsLeft = TextUtils.TruncateToWidth(statsLeft, width, "...");
            statsLeftWidth = TextUtils.VisibleWidth(statsLeft);
        }

        // Calculate available space for padding (minimum 2 spaces between stats and model)
        const int minPadding = 2;

        // Add thinking level indicator if model supports reasoning
        var rightSideWithoutProvider = modelName;
        if (Truthy(stateModel?["reasoning"]))
        {
            var thinkingLevel = session.StateThinkingLevel is { Length: > 0 } level ? level : "off";
            rightSideWithoutProvider = thinkingLevel == "off" ? $"{modelName} • thinking off" : $"{modelName} • {thinkingLevel}";
        }
        // A virtual model routes each request; show where the latest response went.
        if (session.RoutedModel is { } routed)
        {
            var level = routed.ThinkingLevel is { Length: > 0 } routedLevel ? $" • {routedLevel}" : "";
            rightSideWithoutProvider += $" → {Str(routed.Model["id"])}{level}";
        }

        // Prepend the provider in parentheses if there are multiple providers and there's enough room
        var rightSide = rightSideWithoutProvider;
        if (footerData.GetAvailableProviderCount() > 1 && stateModel is not null)
        {
            rightSide = $"({provider}) {rightSideWithoutProvider}";
            if (statsLeftWidth + minPadding + TextUtils.VisibleWidth(rightSide) > width)
            {
                // Too wide, fall back
                rightSide = rightSideWithoutProvider;
            }
        }

        var rightSideWidth = TextUtils.VisibleWidth(rightSide);
        var totalNeeded = statsLeftWidth + minPadding + rightSideWidth;

        string statsLine;
        if (totalNeeded <= width)
        {
            // Both fit - add padding to right-align model
            var padding = new string(' ', width - statsLeftWidth - rightSideWidth);
            statsLine = statsLeft + padding + rightSide;
        }
        else
        {
            // Need to truncate right side
            var availableForRight = width - statsLeftWidth - minPadding;
            if (availableForRight > 0)
            {
                var truncatedRight = TextUtils.TruncateToWidth(rightSide, availableForRight, "");
                var truncatedRightWidth = TextUtils.VisibleWidth(truncatedRight);
                var padding = new string(' ', Math.Max(0, width - statsLeftWidth - truncatedRightWidth));
                statsLine = statsLeft + padding + truncatedRight;
            }
            else
            {
                // Not enough space for right side at all
                statsLine = statsLeft;
            }
        }

        // Apply dim to each part separately. statsLeft may contain color codes (for context %)
        // that end with a reset, which would clear an outer dim wrapper. So we dim the parts
        // before and after the colored section independently.
        var dimStatsLeft = theme.Fg("dim", statsLeft);
        var remainder = statsLine[statsLeft.Length..]; // padding + rightSide
        var dimRemainder = theme.Fg("dim", remainder);

        var pwdLine = TextUtils.TruncateToWidth(theme.Fg("dim", pwd), width, theme.Fg("dim", "..."));
        var lines = new List<string> { pwdLine, dimStatsLeft + dimRemainder };

        // Add extension statuses on a single line, sorted by key alphabetically
        var extensionStatuses = footerData.GetExtensionStatuses();
        if (extensionStatuses.Count > 0)
        {
            var sortedStatuses = extensionStatuses
                .OrderBy(entry => entry.Key, StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.None))
                .Select(entry => SanitizeStatusText(entry.Value));
            var statusLine = string.Join(" ", sortedStatuses);
            // Truncate to terminal width with dim ellipsis for consistency with footer style
            lines.Add(TextUtils.TruncateToWidth(statusLine, width, theme.Fg("dim", "...")));
        }

        return lines;
    }
}
