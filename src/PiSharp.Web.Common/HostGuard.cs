// PiSharp-only (decision 0008, PLAN.md §6 Security), after durable-pi's hub guard (apps/dpi-playground/apps/dpi-playground-hub/src/guard.ts,
// MIT); shared with the Web Agent by decision 0011, which moved its hosts and ports into HostGuardOptions. A loopback app holds secrets (the
// hub's device tokens, the Web Agent's provider keys), so it answers only this computer: a Host header it does not know is a misdirected
// request (421, which stops DNS rebinding), a write from another origin is refused (403, which stops cross-site requests), and an API write
// must be JSON (415, which a plain HTML form cannot send). An answer it lets through carries the app's Content-Security-Policy, if it has one.
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace PiSharp.Web.Common;

/// <summary>Host, Origin and content-type checks, as middleware (<see cref="WebCommonExtensions.UseHostGuard"/>).</summary>
public sealed class HostGuard
{
    private readonly RequestDelegate next;
    private readonly HostGuardOptions options;
    private readonly HashSet<string> hosts;
    private readonly HashSet<int> ports;
    private readonly PathString[] jsonPaths;

    /// <summary>A guard in front of <paramref name="next"/>, with its options read once.</summary>
    /// <exception cref="ArgumentException">A <see cref="HostGuardOptions.JsonPaths"/> entry does not start with <c>/</c>.</exception>
    public HostGuard(RequestDelegate next, IOptions<HostGuardOptions> options)
    {
        this.next = next;
        this.options = options.Value;
        hosts = new HashSet<string>(HostGuardOptions.LoopbackHosts.Concat(this.options.AllowedHosts.Select(host => host.Trim().Trim('[', ']'))),
            StringComparer.OrdinalIgnoreCase);
        ports = [.. this.options.AllowedPorts];
        jsonPaths = [.. this.options.JsonPaths.Select(path => new PathString(path))];
    }

    /// <summary>Answers 421, 403 or 415 itself, or passes the request on with the Content-Security-Policy header.</summary>
    public async Task InvokeAsync(HttpContext http)
    {
        var host = http.Request.Host;
        if (!hosts.Contains(host.Host.Trim('[', ']')) || (ports.Count > 0 && !ports.Contains(host.Port ?? (http.Request.IsHttps ? 443 : 80))))
        {
            http.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            await http.Response.WriteAsync(options.MisdirectedMessage).ConfigureAwait(false);
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
            if (jsonPaths.Any(path => http.Request.Path.StartsWithSegments(path)) &&
                (http.Request.ContentLength > 0 || http.Request.Headers.TransferEncoding.Count > 0) &&
                !(http.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                http.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                await http.Response.WriteAsync("API writes must be JSON.").ConfigureAwait(false);
                return;
            }
        }
        if (options.ContentSecurityPolicy is { Length: > 0 } policy) http.Response.Headers.ContentSecurityPolicy = policy;
        await next(http).ConfigureAwait(false);
    }
}
