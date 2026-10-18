// PiSharp-only (decision 0008, PLAN.md §6 Security), after durable-pi's hub guard (apps/dpi-playground/apps/dpi-playground-hub/src/guard.ts,
// MIT). The hub holds device tokens, and a device token is a shell, so it answers only this computer: a Host header it does not know is a
// misdirected request (421, which stops DNS rebinding), a write from another origin is refused (403, which stops cross-site requests),
// and an API write must be JSON (415, which a plain HTML form cannot send).
namespace PiSharp.Durable.Web.Hub;

/// <summary>Host, Origin and content-type checks.</summary>
public sealed class HostGuard(RequestDelegate next, HubOptions options)
{
    public async Task InvokeAsync(HttpContext http)
    {
        var host = http.Request.Host;
        var name = host.Host.Trim('[', ']');
        if (!Allowed(name))
        {
            http.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            await http.Response.WriteAsync("Misdirected request: this hub answers only localhost.").ConfigureAwait(false);
            return;
        }
        var method = http.Request.Method;
        var write = !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);
        if (write)
        {
            var origin = http.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed) || !string.Equals(parsed.Authority, host.Value, StringComparison.OrdinalIgnoreCase)))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                await http.Response.WriteAsync("Cross-origin writes are refused.").ConfigureAwait(false);
                return;
            }
            if (http.Request.Path.StartsWithSegments("/api") && (http.Request.ContentLength > 0 || http.Request.Headers.TransferEncoding.Count > 0) &&
                !(http.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                http.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                await http.Response.WriteAsync("API writes must be JSON.").ConfigureAwait(false);
                return;
            }
        }
        await next(http).ConfigureAwait(false);
    }

    private bool Allowed(string name) =>
        name is "localhost" or "127.0.0.1" or "::1" || options.AllowedHosts.Contains(name, StringComparer.OrdinalIgnoreCase);
}
