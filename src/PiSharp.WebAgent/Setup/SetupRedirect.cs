// Decision 0011 (run W3, SU): the first-run redirect, a middleware of the Setup area rather than a change to the chat pages. While nothing
// is connected and the wizard was never finished or skipped (SetupState.RedirectsToSetup), a GET of the app's front doors (/, /chat and
// /chat/new: the pages a new chat starts on) answers 302 to /setup. Every other page, every API and every static file passes untouched, so
// Settings, an open chat and the wizard's own calls work during the first run. The redirect is never cached, so it ends as soon as a
// provider is connected or the wizard ends, and with --faux it never applies.
namespace PiSharp.WebAgent.Setup;

/// <summary>The first-run redirect to <see cref="SetupModule.SetupPath"/>.</summary>
public static class SetupRedirect
{
    /// <summary>The paths the redirect applies to: the home page and the new chat page (with its <c>/chat</c> alias).</summary>
    public static IReadOnlyList<string> Paths { get; } = ["/", "/chat", "/chat/new"];

    /// <summary>Whether <paramref name="request"/> is a page load the redirect applies to: GET or HEAD of one of <see cref="Paths"/>
    /// (case-insensitive, a trailing slash allowed).</summary>
    public static bool Applies(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)) return false;
        var path = request.Path.Value;
        if (string.IsNullOrEmpty(path)) return true;
        var trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
        if (trimmed.Length == 0) trimmed = "/";
        return Paths.Contains(trimmed, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Adds the redirect to the pipeline (after HostGuard and the static files, before the endpoints).</summary>
    public static IApplicationBuilder UseSetupRedirect(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            if (Applies(context.Request) && context.RequestServices.GetRequiredService<SetupState>().RedirectsToSetup())
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Redirect(SetupModule.SetupPath);
                return;
            }
            await next(context).ConfigureAwait(false);
        });
    }
}
