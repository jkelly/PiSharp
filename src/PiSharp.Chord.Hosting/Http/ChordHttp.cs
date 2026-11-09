using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using PiSharp.Chord.Registries;
using PiSharp.Chord.Hosting.Plugins;

namespace PiSharp.Chord.Hosting.Http;

/// <summary>A registry-driven request handler, optionally with its own loopback-first Kestrel listener.</summary>
public sealed class ChordHttp : IAsyncDisposable
{
    private readonly ChordHost host;
    private readonly ChordHttpOptions options;
    private readonly object sync = new();
    private readonly Dictionary<CancellationTokenSource, TaskCompletionSource> running = [];
    private sealed record SocketAdmission(WebSocket Socket, HttpRequestContext Request, HttpSocket? Handler);
    private readonly Dictionary<WebSocket, SocketAdmission> sockets = [];
    private readonly CancellationTokenSource stopped = new();
    private Task? routerWatching;
    private readonly TaskCompletionSource<Uri> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication? app;
    private Task? closing;
    private static readonly Contract Listener = Contract.Local("chord.http.listener", new() { Version = 1, Params = [], Members = [] });
    public Facet Facet { get; }
    public ChordHttp(ChordHost host, ChordHttpOptions options)
    {
        this.host = host; this.options = options;
        if (options.Port is < 0 or > 65535 || options.CloseTimeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        Facet = PiSharp.Chord.Host.Facet.Define((facet, _) =>
        {
            facet.Serve(HttpRegistries.Http, new RegistryProvider<HttpContribution, HttpRouter> { Compose = HttpRouter.Compose });
            facet.Serve(HttpRegistries.Authenticators, new RegistryProvider<HttpAuthenticator, IReadOnlyList<HttpAuthenticator>> { Compose = (entries, _) => new HttpAuthenticators(entries) });
            if (options.Port is not null) facet.Serve(Listener, (_, _, self) =>
            {
                self.Open(OpenAsync, async (application, _, context) => { await CloseAsync(context).ConfigureAwait(false); await application.DisposeAsync().ConfigureAwait(false); });
                return new(new Dictionary<string, object?>());
            });
        });
    }
    public Task<Uri> ListeningAsync(ChordContext context) => options.Port is null
        ? Task.FromException<Uri>(new InvalidOperationException("No HTTP listener was configured"))
        : listening.Task.WaitAsync(context.AbortSignal ?? CancellationToken.None);
    private async Task<WebApplication> OpenAsync(ChordContext context)
    {
        WebApplication? application = null;
        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            options.Configure?.Invoke(builder);
            builder.WebHost.ConfigureKestrel(server =>
            {
                server.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
                server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(60);
                server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5);
                server.Listen(options.Address, options.Port!.Value);
            });
            application = builder.Build();
            application.UseWebSockets(new() { KeepAliveInterval = TimeSpan.FromSeconds(15), KeepAliveTimeout = TimeSpan.FromSeconds(15) });
            application.Run(HandleAsync);
            await application.StartAsync(context.AbortSignal ?? CancellationToken.None).ConfigureAwait(false);
            lock (sync) { app = application; }
            var address = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            listening.TrySetResult(new Uri(address));
            return application;
        }
        catch (Exception error)
        {
            if (application is not null) { try { await application.DisposeAsync().ConfigureAwait(false); } catch (Exception cleanup) { Report(new("listener_cleanup_failed", cleanup)); } }
            lock (sync) if (ReferenceEquals(app, application)) app = null;
            listening.TrySetException(error); throw;
        }
    }

    /// <summary>Mount on an application's existing ASP.NET pipeline; the connection's real local address is used for admission.</summary>
    public async Task HandleAsync(HttpContext native)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(native.RequestAborted);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            if (closing is not null) { native.Response.StatusCode = 503; ApplyDefaults(native, null, null); return; }
            running.Add(cancellation, done);
        }
        var context = ChordContext.WithAbortSignal(cancellation.Token, ChordContext.Background);
        HttpRequestContext? request = null;
        HttpRouter? responseRouter = null;
        try
        {
            if (native.Request.Method is "TRACE" or "TRACK" or "CONNECT") throw new HttpFailure(501);
            var registry = await host.RegistryAsync(HttpRegistries.Http, context).ConfigureAwait(false);
            lock (sync) routerWatching ??= WatchRouterAsync(registry);
            var router = registry.Value;
            responseRouter = router;
            var routerCallbacks = router.Routes.Select(item => (Delegate)item.Value.Handler)
                .Concat(router.Wraps.Select(item => (Delegate)item.Value.Wrap)).Concat(router.Sockets.Select(item => (Delegate)item.Value))
                .Concat(router.Relays.Select(item => (Delegate)item.Value.Verify)).ToArray();
            using var routerLease = PluginCallbacks.Retain(routerCallbacks, router.CallbackOwners);
            var (origin, provenance, framer) = await AdmitAsync(native, router, routerCallbacks, context).ConfigureAwait(false);
            var (route, parameters) = router.Match(native.Request.Method, native.Request.Path.Value ?? "/");
            var isLink = options.LinksPath is { } links && native.Request.Path == links && native.WebSockets.IsWebSocketRequest;
            var socketRoute = native.WebSockets.IsWebSocketRequest ? router.Sockets.OrderByDescending(x => x.Path.Rank, StringComparer.Ordinal).Select(x => (Route: x, Args: x.Path.Match(native.Request.Path.Value ?? "/"))).FirstOrDefault(x => x.Args is not null) : default;
            if (socketRoute.Args is not null) parameters = socketRoute.Args;
            request = new(native, origin, provenance, parameters, framer);
            var foreign = IsForeign(native.Request, origin);
            var crossSite = route?.Value.CrossSite == true && !native.WebSockets.IsWebSocketRequest;
            var destination = native.Request.Headers["Sec-Fetch-Dest"].ToString();
            var pageNavigation = route?.Value.Page == true && native.Request.Method == "GET" && native.Request.Headers["Sec-Fetch-Mode"] == "navigate" && destination is "document" or "empty";
            if (!crossSite && !OriginAccepted(native.Request, origin)) throw new HttpFailure(403);
            System.Text.Json.Nodes.JsonObject? principal = null;
            if (!crossSite && (!foreign || pageNavigation))
            {
                var authenticators = (await host.RegistryAsync(HttpRegistries.Authenticators, context).ConfigureAwait(false)).Value;
                var authenticatorOwners = (authenticators as HttpAuthenticators)?.CallbackOwners;
                using var authenticatorLeases = PluginCallbacks.Retain(authenticators.Cast<Delegate>(), authenticatorOwners);
                foreach (var authenticate in authenticators)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    try { principal = await PluginCallbacks.InvokeAsync(authenticate, () => authenticate(request, context), authenticators.Cast<Delegate>(), authenticatorOwners).WaitAsync(cancellation.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { Report(new("hook_failed", error, Pattern: "authenticate")); }
                    if (principal is not null) break;
                }
            }
            ApplyDefaults(native, request, router);
            if (native.WebSockets.IsWebSocketRequest)
            {
                if (principal is null || (!isLink && socketRoute.Route is null)) throw new HttpFailure(principal is null ? 403 : 404);
                await host.RootAsync(async call =>
                {
                    var current = registry.Value;
                    if (!StillAdmitted(new(null!, request, isLink ? null : socketRoute.Route!.Value), current)) throw new HttpFailure(403);
                    using var socket = await native.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                    lock (sync) sockets.Add(socket, new(socket, request, isLink ? null : socketRoute.Route!.Value));
                    try
                    {
                        if (isLink)
                        {
                            var link = WebSocketLink.FromWebSocket(socket, 1024 * 1024);
                            var linkOptions = options.LinkOptions?.Invoke(request, principal) ?? new LinkOptions { Principal = principal };
                            host.Accept(link, linkOptions, call.Context);
                            while (socket.State is WebSocketState.Open or WebSocketState.CloseSent) await Task.Delay(100, call.AbortSignal).ConfigureAwait(false);
                        }
                        else
                        {
                            using var limited = new LimitedWebSocket(socket, socketRoute.Route!.MaxPayload);
                            await PluginCallbacks.InvokeAsync(socketRoute.Route.Value, () => socketRoute.Route.Value(limited, request, call), routerCallbacks, router.CallbackOwners).WaitAsync(call.AbortSignal).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { socket.Abort(); }
                    catch (Exception error) { Report(new("socket_failed", error, socketRoute.Route?.Plugin, socketRoute.Route?.Pattern)); socket.Abort(); }
                    finally { lock (sync) sockets.Remove(socket); }
                    return true;
                }, context, new RootOptions { Principal = principal }).ConfigureAwait(false);
                return;
            }
            if (route is null) throw new HttpFailure(404);
            await host.RootAsync(async call =>
            {
                Func<Task<HttpResult>> next = () => PluginCallbacks.InvokeAsync(route.Value.Handler, () => route.Value.Handler(request, call));
                foreach (var wrapper in router.Wraps.Where(x => (x.Value.All || x.Plugin == route.Plugin) && (x.Value.Route is null || x.Value.Route == route.Pattern)).Reverse())
                {
                    var inner = next;
                    next = async () =>
                    {
                        try { return await PluginCallbacks.InvokeAsync(wrapper.Value.Wrap, () => wrapper.Value.Wrap(request, inner, call)).ConfigureAwait(false); }
                        catch (HttpFailure) { throw; }
                        catch (Exception error) { Report(new("wrap_failed", error, wrapper.Plugin, wrapper.Value.Name)); throw new HttpFailure(500); }
                    };
                }
                HttpResult result;
                try { result = await PluginCallbacks.InvokeWithDependenciesAsync(routerCallbacks, next, router.CallbackOwners).WaitAsync(call.AbortSignal).ConfigureAwait(false); }
                catch (HttpFailure) { throw; }
                catch (ChordError error) when (error.Code == "forbidden") { throw new HttpFailure(403); }
                catch (Exception error) when (error is not OperationCanceledException) { Report(new("route_failed", error, route.Plugin, route.Pattern)); throw new HttpFailure(500); }
                try { await HttpResponses.WriteAsync(native, request, result, call, routerCallbacks, router.CallbackOwners).ConfigureAwait(false); }
                catch (HttpFailure) { throw; }
                catch (Exception error) when (error is not OperationCanceledException) { Report(new("response_failed", error, route.Plugin, route.Pattern)); throw; }
                return true;
            }, context, new RootOptions { Principal = principal }).ConfigureAwait(false);
        }
        catch (HttpFailure error) { if (!native.Response.HasStarted) { native.Response.StatusCode = error.Status; ApplyDefaults(native, request, responseRouter); } else native.Abort(); }
        catch (OperationCanceledException) { if (!native.Response.HasStarted) { native.Response.StatusCode = 503; ApplyDefaults(native, request, responseRouter); } else native.Abort(); }
        catch (Exception error) { Report(new("response_failed", error)); if (!native.Response.HasStarted) { native.Response.StatusCode = error is ChordError ? 503 : 500; ApplyDefaults(native, request, responseRouter); } else native.Abort(); }
        finally { lock (sync) running.Remove(cancellation); done.TrySetResult(); }
    }
    private async Task<(string Origin, HttpProvenance Provenance, string? Framer)> AdmitAsync(HttpContext native, HttpRouter router, IReadOnlyList<Delegate> dependencies, ChordContext context)
    {
        var request = native.Request; var hosts = request.Headers.Host;
        if (hosts.Count != 1 || string.IsNullOrWhiteSpace(hosts[0]) || hosts[0]!.Any(x => x is '@' or '/' or '\\' or ',' or '\r' or '\n' or '\0')) throw new HttpFailure(403);
        if (native.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget is { } target &&
            (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
            Uri.TryCreate(target, UriKind.Absolute, out _)) throw new HttpFailure(403);
        var forwarded = request.Headers.Keys.Any(x => x.Equals("Forwarded", StringComparison.OrdinalIgnoreCase) || x.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase));
        var localAddress = native.Connection.LocalIpAddress;
        var local = localAddress is not null && IPAddress.IsLoopback(localAddress);
        var hostName = request.Host.Host;
        var loopbackName = hostName.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(hostName.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
        if (local && !forwarded && loopbackName && (request.Host.Port ?? 80) == native.Connection.LocalPort) return ($"http://{request.Host}", new("local"), null);
        if (local && forwarded)
        {
            foreach (var entry in router.Relays)
            {
                try
                {
                    var admitted = await PluginCallbacks.InvokeAsync(entry.Value.Verify, () => entry.Value.Verify(request, context), dependencies, router.CallbackOwners).WaitAsync(context.AbortSignal ?? CancellationToken.None).ConfigureAwait(false);
                    if (admitted is null) continue;
                    if (!HttpRouter.IsOrigin(admitted.Origin) || admitted.Framer is not null && !HttpRouter.IsOrigin(admitted.Framer)) throw new InvalidDataException("relay did not return exact origins");
                    return (admitted.Origin, new("relayed", entry.Value.Name), admitted.Framer);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { Report(new("hook_failed", error, entry.Plugin, "relay")); }
            }
        }
        var admittedHost = request.Host.Value;
        if (string.IsNullOrEmpty(admittedHost) || !router.Hosts.Contains(admittedHost)) throw new HttpFailure(403);
        var remoteOrigin = "https://" + admittedHost;
        if (!Uri.TryCreate(remoteOrigin, UriKind.Absolute, out var remoteUri) || remoteUri.UserInfo.Length != 0 || remoteUri.AbsolutePath != "/" || remoteUri.Query.Length != 0 || remoteUri.Fragment.Length != 0) throw new HttpFailure(403);
        return (remoteUri.GetLeftPart(UriPartial.Authority), new("remote"), null);
    }
    internal static bool IsForeign(HttpRequest request, string origin)
    {
        var site = request.Headers["Sec-Fetch-Site"].ToString(); var destination = request.Headers["Sec-Fetch-Dest"].ToString();
        return request.Headers.ContainsKey("Sec-Fetch-Site") && site is not ("same-origin" or "none") ||
            site != "same-origin" && destination is "iframe" or "frame" or "embed" or "object" or "fencedframe";
    }
    private static bool OriginAccepted(HttpRequest request, string origin)
    {
        if (request.Headers.TryGetValue("Origin", out var value) && (value.Count != 1 || value != origin)) return false;
        var unsafeMethod = request.Method is not ("GET" or "HEAD" or "OPTIONS");
        return !unsafeMethod || !request.Headers.ContainsKey("Sec-Fetch-Site") || value == origin;
    }
    private async Task WatchRouterAsync(IRegistryValue<HttpRouter> registry)
    {
        var watch = await registry.WatchAsync(ChordContext.WithAbortSignal(stopped.Token, ChordContext.Background)).ConfigureAwait(false);
        watch.Start((call, _) =>
        {
            if (call is NativeWatchValueCall<HttpRouter> value)
            {
                SocketAdmission[] active; lock (sync) active = sockets.Values.ToArray();
                foreach (var admission in active) if (!StillAdmitted(admission, value.Value)) admission.Socket.Abort();
            }
            return Task.CompletedTask;
        });
    }
    private static bool StillAdmitted(SocketAdmission admission, HttpRouter router)
    {
        if (admission.Request.Provenance.Kind == "remote" && (admission.Request.Native.Request.Host.Value is not { Length: > 0 } hostValue || !router.Hosts.Contains(hostValue))) return false;
        if (admission.Request.Provenance.Kind == "relayed" && !router.Relays.Any(x => x.Value.Name == admission.Request.Provenance.Relay)) return false;
        return admission.Handler is null || router.Sockets.Any(x => ReferenceEquals(x.Value, admission.Handler) && x.Path.Match(admission.Request.Path) is not null);
    }
    private static void ApplyDefaults(HttpContext native, HttpRequestContext? request, HttpRouter? router)
    {
        if (native.Response.HasStarted) return;
        var headers = native.Response.Headers;
        headers.CacheControl = "no-store"; headers.XContentTypeOptions = "nosniff"; headers["Referrer-Policy"] = "no-referrer";
        var framers = (router?.Framers.AsEnumerable() ?? []).Concat(request?.Framer is { } framer ? [framer] : []).Distinct().ToArray();
        if (framers.Length == 0) headers.XFrameOptions = "DENY"; else headers.Remove("X-Frame-Options");
        var csp = new Dictionary<string, string> { ["default-src"] = "'none'", ["script-src"] = $"'self' 'nonce-{request?.Nonce ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))}'", ["connect-src"] = "'self'", ["base-uri"] = "'none'", ["form-action"] = "'self'", ["frame-ancestors"] = framers.Length > 0 ? string.Join(' ', framers) : "'none'" };
        if (router is not null) foreach (var (name, sources) in router.Csp) csp[name] = csp.GetValueOrDefault(name, "") + " " + string.Join(' ', sources);
        headers.ContentSecurityPolicy = string.Join("; ", csp.Select(x => x.Key + " " + x.Value));
    }
    private void Report(HttpError error) { try { options.OnError(error); } catch { } }
    public Task CloseAsync(ChordContext context)
    {
        lock (sync) return closing ??= CloseCoreAsync(context);
    }
    private async Task CloseCoreAsync(ChordContext context)
    {
        await Task.Yield();
        Task[] tasks; WebSocket[] active;
        stopped.Cancel();
        lock (sync) { tasks = running.Values.Select(x => x.Task).ToArray(); active = sockets.Keys.ToArray(); }
        foreach (var socket in active) socket.Abort();
        try { await Task.WhenAll(tasks).WaitAsync(options.CloseTimeout, context.AbortSignal ?? CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException) { }
        CancellationTokenSource[] remaining; lock (sync) remaining = running.Keys.ToArray();
        foreach (var source in remaining) try { source.Cancel(); } catch (ObjectDisposedException) { }
        if (app is not null) { using var timeout = new CancellationTokenSource(options.CloseTimeout); await app.StopAsync(timeout.Token).ConfigureAwait(false); }
    }
    public ValueTask DisposeAsync() => new(CloseAsync(ChordContext.Background));
}
