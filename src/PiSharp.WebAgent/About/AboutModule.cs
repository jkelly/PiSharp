// Decision 0011 (run W3, unit AP): the About area (Settings › About). It registers AboutFacts, ConnectivityCheck and AboutService and maps
// /api/about: the diagnostics report as text (a download with ?download=1), and the provider connectivity check, which only a POST runs (a
// GET answers its targets and last run and sends nothing). Writes are POSTs without a body, so HostGuard's cross-origin rule (403) covers
// them. Every answer is Cache-Control: no-store and holds no secret. Needs the credentials area (IAccountStore, IConnectionTester) and an
// IModelCatalog; call it after the area modules, before AddWebAgentFallbacks or after (the catalog is resolved on first use).
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.About;

/// <summary>Registers and maps the About area.</summary>
public static class AboutModule
{
    /// <summary>The route prefix.</summary>
    public const string Prefix = "/api/about";

    /// <summary>The partial with a check's results (the results pane of <c>_AboutConnectivityCheck</c>).</summary>
    public const string ResultsPartial = "~/Views/Settings/_AboutConnectivityResults.cshtml";

    /// <summary>Registers <see cref="AboutFacts"/>, <see cref="ConnectivityCheck"/> and <see cref="AboutService"/>.</summary>
    public static IServiceCollection AddAboutArea(this IServiceCollection services, WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton(provider => new AboutFacts(provider.GetRequiredService<WebAgentOptions>(), provider.GetService<IServer>(),
            provider.GetService<TimeProvider>()));
        services.TryAddSingleton(provider => new ConnectivityCheck(provider.GetRequiredService<IAccountStore>(), provider.GetRequiredService<IConnectionTester>(),
            provider.GetRequiredService<IModelCatalog>(), provider.GetRequiredService<WebAgentOptions>(), provider.GetService<CredentialsOptions>(),
            provider.GetService<TimeProvider>()));
        services.TryAddSingleton(provider => new AboutService(provider.GetRequiredService<AboutFacts>(), provider.GetRequiredService<IModelCatalog>(),
            provider.GetRequiredService<IAccountStore>(), provider.GetRequiredService<ISettingsStore>(), provider.GetRequiredService<ConnectivityCheck>(),
            provider.GetRequiredService<WebAgentOptions>(), provider.GetService<TimeProvider>()));
        return services;
    }

    /// <summary>Maps <c>/api/about</c>.</summary>
    public static WebApplication MapAboutArea(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var api = app.MapGroup(Prefix);
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });

        // ?download=1 (or true) answers it as an attachment.
        api.MapGet("/diagnostics", (string? download, AboutService about, HttpContext http) =>
        {
            var page = about.Page();
            if (download is "1" || string.Equals(download, "true", StringComparison.OrdinalIgnoreCase))
                http.Response.Headers.ContentDisposition = $"attachment; filename=\"{DiagnosticsReport.FileName(page.GeneratedAt)}\"";
            return Results.Text(page.Report, "text/plain; charset=utf-8");
        });

        api.MapGet("/connectivity", (AboutService about) => Results.Json(Answer(about.Connectivity(), null, null), SettingsStore.JsonOptions));

        api.MapPost("/connectivity", async (ConnectivityCheck check, AboutService about, FragmentRenderer renderer, CancellationToken token) =>
        {
            await check.RunAsync(token).ConfigureAwait(false);
            var view = about.Connectivity();
            var html = await renderer.RenderAsync(ResultsPartial, view).ConfigureAwait(false);
            return Results.Json(Answer(view, html, about.Report()), SettingsStore.JsonOptions);
        });
        return app;
    }

    private static ConnectivityAnswer Answer(AboutConnectivityView view, string? html, string? report) => new(view.Offline,
        [.. view.Targets.Select(target => new ConnectivityTargetAnswer(target.Provider, target.Name, target.AccountId, Kind(target.Kind)))],
        view.Last is { } run ? new ConnectivityRunAnswer(run.CheckedAt, run.Offline, run.Message, run.Summary, run.Reachable, run.Tested,
            [.. run.Results.Select(result => new ConnectivityResultAnswer(result.Provider, result.Name, result.AccountId, Kind(result.Kind),
                DiagnosticsReport.Outcome(result.Outcome), result.Detail, result.StatusCode, result.LatencyMs, result.ModelCount))]) : null,
        html, report);

    private static string Kind(AccountKind kind) => kind switch
    {
        AccountKind.ApiKey => "apiKey",
        AccountKind.OAuth => "oauth",
        AccountKind.Environment => "environment",
        _ => "ambient",
    };
}

/// <summary>GET and POST /api/about/connectivity.</summary>
/// <param name="Offline">Whether the app is offline (<c>--faux</c>): a check sends nothing.</param>
/// <param name="Targets">What a check tests: each provider's active account.</param>
/// <param name="Last">The last check since the server started, or null.</param>
/// <param name="Html">POST only: the results pane, rendered.</param>
/// <param name="Report">POST only: the diagnostics report, with the check.</param>
public sealed record ConnectivityAnswer(bool Offline, IReadOnlyList<ConnectivityTargetAnswer> Targets, ConnectivityRunAnswer? Last, string? Html, string? Report);

/// <summary>A provider a check tests.</summary>
public sealed record ConnectivityTargetAnswer(string Provider, string Name, string AccountId, string Kind);

/// <summary>A check.</summary>
public sealed record ConnectivityRunAnswer(DateTimeOffset CheckedAt, bool Offline, string? Message, string Summary, int Reachable, int Tested,
    IReadOnlyList<ConnectivityResultAnswer> Results);

/// <summary>One provider's result; <c>outcome</c> is <c>ok</c>, <c>slow</c>, <c>failed</c> or <c>notTested</c>.</summary>
public sealed record ConnectivityResultAnswer(string Provider, string Name, string AccountId, string Kind, string Outcome, string Detail, int? StatusCode,
    long? LatencyMs, int? ModelCount);
