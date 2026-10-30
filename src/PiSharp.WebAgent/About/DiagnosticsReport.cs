// Decision 0011 (run W3, unit AP): Settings › About's diagnostics report, plain text to paste into a bug report. It holds versions, the host,
// folders (paths are fine), the catalog's numbers, each account's provider, kind, status and where its credential comes from, the non-secret
// settings that change behaviour, and the last connectivity check. It never holds an API key, a token, a key hint, an account's label, email
// or identity, an allow-list command or any message content: an account is described only by metadata IAccountStore gives every page, its
// base URL is cut to scheme and host, and every free text that came from outside (a status reason, a test's message) is scrubbed again.
using System.Globalization;
using System.Text;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;

namespace PiSharp.WebAgent.About;

/// <summary>Writes the diagnostics report.</summary>
public static class DiagnosticsReport
{
    /// <summary>The report's first line, before its time.</summary>
    public const string Title = "PiSharp Web Agent diagnostics";

    /// <summary>The line under the title.</summary>
    public const string Promise = "No API key, token, account name or message content is included.";

    private const int KeyWidth = 16;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The report of <paramref name="page"/>.</summary>
    public static string Write(AboutPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var text = new StringBuilder();
        var versions = page.Versions;
        var host = page.Host;
        text.Append(Title).Append(" · ").AppendLine(Stamp(page.GeneratedAt));
        text.AppendLine(Promise);
        text.AppendLine();

        var app = new List<string> { versions.App, versions.Configuration };
        if (versions.Commit is { } commit) app.Add("commit " + commit);
        if (versions.Built is { } built) app.Add("built " + built.ToString("yyyy-MM-dd HH:mm", Invariant) + " UTC");
        Line(text, "app", string.Join(" · ", app));
        Line(text, "pisharp", $"{versions.PiSharp} (tracks Pi {versions.TracksPi})");
        Line(text, "runtime", $"{versions.Runtime} · ASP.NET Core {versions.AspNetCore}");
        Line(text, "os", host.Os);
        Line(text, "listen", host.Addresses.Count == 0 ? "unknown" : string.Join(", ", host.Addresses) + (host.LoopbackOnly ? " (loopback only)" : " (NOT loopback only)"));
        Line(text, "uptime", AboutFacts.Duration(host.Uptime));
        Line(text, "mode", host.Faux ? "offline (--faux): faux models only, nothing is sent to a provider" : "live providers");
        Line(text, "data", page.Options.DataDirectory);
        Line(text, "chats", page.Options.DeviceDirectory);
        Line(text, "database", page.Options.DatabasePath);
        Line(text, "credentials", page.Options.CredentialsPath + " (encrypted; not read for this report)");

        var catalog = page.Catalog;
        Line(text, "catalog", string.Create(Invariant, $"{catalog.Providers:N0} providers · {catalog.ChatModels:N0} chat models · {catalog.Reasoning:N0} reasoning · {catalog.ImageInput:N0} read images · {catalog.Hidden:N0} hidden"));
        if (catalog.Builtin is { } builtin)
            Line(text, "built-in", string.Create(Invariant, $"{builtin.Shards:N0} shards (SHA-256 verified) · {builtin.Providers:N0} providers · {builtin.ChatModels:N0} chat · {builtin.ImageModels:N0} image · {builtin.Classifiers:N0} classifier · manifest {builtin.ShortManifest}"));
        else
            Line(text, "built-in", "not read (--faux)");
        if (catalog.NotBuiltin.Count > 0) Line(text, "not built in", string.Join(", ", catalog.NotBuiltin));
        text.AppendLine();

        text.AppendLine("accounts (secrets, names and emails omitted)");
        if (page.Accounts.Count == 0) text.AppendLine("(none)");
        foreach (var account in page.Accounts.OrderBy(account => account.Provider, StringComparer.Ordinal).ThenByDescending(account => account.IsActive))
            text.AppendLine(Account(account, page.GeneratedAt));
        text.AppendLine();

        text.AppendLine("settings");
        var defaults = page.Defaults;
        Line(text, "defaultModel", defaults.Provider is { } provider ? provider + "/" + (defaults.Model ?? "(provider default)") : "not set");
        Line(text, "defaultThinking", defaults.Thinking ?? "model default");
        var rules = page.Tools.AllowList?.Count ?? 0;
        Line(text, "toolApproval", page.Tools.ApprovalMode switch
        {
            ToolApprovalMode.Ask => "ask",
            ToolApprovalMode.AllowList => $"allowList ({(rules == 1 ? "1 rule" : rules.ToString(Invariant) + " rules")})",
            ToolApprovalMode.Auto => "auto (never asks)",
            _ => page.Tools.ApprovalMode.ToString(),
        });
        Line(text, "cwd", page.Tools.EffectiveWorkingDirectory);
        text.AppendLine();

        var connectivity = page.Connectivity;
        if (connectivity.Last is not { } run)
        {
            Line(text, "connectivity", "not checked since the server started");
        }
        else
        {
            Line(text, "connectivity", $"checked {Stamp(connectivity.Local(run.CheckedAt))} · {Scrub(run.Summary)}");
            foreach (var result in run.Results)
            {
                var latency = result.LatencyMs is { } ms ? ms.ToString("N0", Invariant) + " ms" : "";
                text.Append("  ").Append(Pad(result.Provider, KeyWidth - 2)).Append(Pad(Outcome(result.Outcome), 11)).Append(Pad(latency, 10))
                    .AppendLine(Scrub(result.Detail));
            }
        }
        return text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>The name of <paramref name="outcome"/> in the report and the API: <c>ok</c>, <c>slow</c>, <c>failed</c>, <c>notTested</c>.</summary>
    public static string Outcome(ConnectivityOutcome outcome) => outcome switch
    {
        ConnectivityOutcome.Ok => "ok",
        ConnectivityOutcome.Slow => "slow",
        ConnectivityOutcome.Failed => "failed",
        _ => "notTested",
    };

    /// <summary>The download's file name: <c>pisharp-webagent-diagnostics-2026-10-29.txt</c>.</summary>
    public static string FileName(DateTimeOffset time) => "pisharp-webagent-diagnostics-" + time.ToString("yyyy-MM-dd", Invariant) + ".txt";

    private static string Account(AccountInfo account, DateTimeOffset now)
    {
        var kind = account.Kind switch
        {
            AccountKind.ApiKey => "apiKey",
            AccountKind.OAuth => "oauth",
            AccountKind.Environment => "environment",
            _ => "ambient",
        };
        var status = account.Status switch
        {
            AccountStatus.Connected => "connected",
            AccountStatus.Expired => "expired",
            _ => "unavailable",
        };
        var facts = new List<string>();
        if (account.Kind is AccountKind.Environment or AccountKind.Ambient && account.Source is { Length: > 0 } source) facts.Add(Scrub(source));
        if (account.Kind == AccountKind.ApiKey) facts.Add("stored, encrypted");
        if (account.Settings.TryGetValue("baseUrl", out var baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            facts.Add("baseUrl " + uri.Scheme + "://" + uri.Authority); // never its user info, path or query
        if (account.Details.TryGetValue("plan", out var plan) && plan.Length is > 0 and <= 40) facts.Add("plan " + Scrub(plan));
        if (account.Details.TryGetValue("host", out var accountHost) && Uri.CheckHostName(accountHost) != UriHostNameType.Unknown) facts.Add("host " + accountHost);
        if (account.ExpiresAt is { } expires)
            facts.Add(expires > now ? "token expires " + expires.ToString("yyyy-MM-dd", Invariant) + " (refreshed before)" : "token expired " + expires.ToString("yyyy-MM-dd", Invariant));
        if (account.Status != AccountStatus.Connected && account.StatusReason is { Length: > 0 } reason) facts.Add(Scrub(reason));
        return Pad(account.Provider, KeyWidth) + Pad(kind, 13) + Pad(status, 13) + Pad(account.IsActive ? "active" : "", 8) + string.Join(" · ", facts);
    }

    private static string Scrub(string text) => SecretText.Scrub(text);

    private static string Stamp(DateTimeOffset time) => time.ToString("yyyy-MM-dd HH:mm:ss zzz", Invariant);

    private static void Line(StringBuilder text, string key, string value) => text.Append(Pad(key, KeyWidth)).AppendLine(value);

    private static string Pad(string value, int width) => value.Length >= width ? value + " " : value.PadRight(width);
}
