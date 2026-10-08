using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Transport;

namespace PiSharp.Extensions.Runtime.Mcp.Authentication;

/// <summary>Explicit authentication over an already admitted physical request factory. At most one
/// retry follows 401 or 403 insufficient_scope. No endpoint/client/token acquisition is performed.</summary>
public static class McpAuthenticatedHttpRequestFactory
{
    public static McpAdmittedHttpRequestFactory Create(McpAdmittedHttpRequestFactory physical,
        Uri exactAdmittedEndpoint, McpAdmittedHttpAuthentication authentication, int maximumBytes = 1_048_576)
    {
        ArgumentNullException.ThrowIfNull(physical); ArgumentNullException.ThrowIfNull(exactAdmittedEndpoint);
        ArgumentNullException.ThrowIfNull(authentication); ArgumentNullException.ThrowIfNull(authentication.Token);
        if (!exactAdmittedEndpoint.IsAbsoluteUri || exactAdmittedEndpoint.Scheme is not ("http" or "https") || maximumBytes < 1)
            throw new ArgumentException("Finite explicit HTTP authentication admission required.");
        if (physical.GetInvocationList().Length != 1 || authentication.Token.GetInvocationList().Length != 1 ||
            authentication.OnUnauthorized?.GetInvocationList().Length > 1)
            throw new ArgumentException("One admitted physical factory and each authentication callback required.");
        return request =>
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.RequestUri != exactAdmittedEndpoint)
                throw new InvalidOperationException("Authentication admission belongs to the exact endpoint.");
            return new Operation(physical, request, exactAdmittedEndpoint, authentication, maximumBytes);
        };
    }
    private sealed class Operation(McpAdmittedHttpRequestFactory physical, HttpRequestMessage template,
        Uri endpoint, McpAdmittedHttpAuthentication auth, int maximumBytes) : IMcpAdmittedHttpRequestOperation
    {
        private sealed class Frame { internal volatile bool Active = true; }
        private sealed class CancellationFrame(Operation owner, CancellationFrame? logicalParent, CancellationFrame? synchronousParent)
        {
            internal readonly Operation Owner = owner;
            internal readonly CancellationFrame? LogicalParent = logicalParent, SynchronousParent = synchronousParent;
            internal volatile bool Active = true;
        }
        private static readonly AsyncLocal<CancellationFrame?> cancellationContext = new();
        [ThreadStatic] private static CancellationFrame? synchronousCancellation;
        private readonly object gate = new();
        private readonly CancellationTokenSource source = new();
        private readonly TaskCompletionSource admission = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AsyncLocal<Frame?> frame = new();
        private readonly List<IMcpAdmittedHttpRequestOperation> leases = [];
        private readonly Dictionary<IMcpAdmittedHttpRequestOperation, Task> physicalStops = new(ReferenceEqualityComparer.Instance);
        private CancellationTokenRegistration callerRegistration;
        private Task<HttpResponseMessage>? send;
        private Task? stop;
        private bool admitted;

        public ValueTask<HttpResponseMessage> SendAsync(CancellationToken token)
        {
            RejectReentry(); token.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (stop is not null || admitted) throw new InvalidOperationException("Authenticated request already consumed or retired.");
                admitted = true;
                try
                {
                    callerRegistration = token.Register(() => { lock (gate) _ = BeginStop(); });
                    if (stop is not null) throw new OperationCanceledException(token);
                    // Publish the owned original before any caller token/challenge code can run.
                    send = SendCoreAsync(); return new(send);
                }
                finally { admission.TrySetResult(); }
            }
        }
        private void RejectReentry()
        {
            if (frame.Value is { Active: true }) throw new InvalidOperationException("Authentication callback cannot join its own request stop.");
            var pending = new Stack<CancellationFrame>();
            if (cancellationContext.Value is { } logical) pending.Push(logical);
            if (synchronousCancellation is { } synchronous) pending.Push(synchronous);
            var seen = new HashSet<CancellationFrame>(ReferenceEqualityComparer.Instance);
            while (pending.Count > 0)
            {
                var current = pending.Pop(); if (!seen.Add(current)) continue;
                if (current.Active && ReferenceEquals(current.Owner, this))
                    throw new InvalidOperationException("Authentication cancellation callback cannot join its own or ancestor request stop.");
                if (current.LogicalParent is { } parent) pending.Push(parent);
                if (current.SynchronousParent is { } nativeParent) pending.Push(nativeParent);
            }
        }
        public Task StopAsync() { RejectReentry(); lock (gate) return BeginStop(); }
        private Task BeginStop()
        {
            if (!admitted) admission.TrySetResult();
            // Capture BOTH ancestries before scheduling: a normal Register callback may restore an
            // old logical ExecutionContext while its actual cancellation TLS ancestry is live.
            var logicalParent = cancellationContext.Value; var synchronousParent = synchronousCancellation;
            return stop ??= Task.Run(() => StopCoreAsync(logicalParent, synchronousParent));
        }
        private static Exception Capture(string phase, Task? original, Exception direct)
            => new McpHttpAuthenticationOriginalException(phase, original, original is { IsFaulted: true } ? original.Exception! : direct, direct);
        private async Task<T> Invoke<T>(string phase, Func<ValueTask<T>> callback)
        {
            Task<T>? original = null; var borrowed = new Frame(); frame.Value = borrowed;
            try { original = callback().AsTask(); return await original.ConfigureAwait(false); }
            catch (OperationCanceledException error) when (original is { IsCanceled: true } && source.IsCancellationRequested && error.CancellationToken == source.Token) { throw; }
            catch (Exception error) { throw Capture(phase, original, error); }
            finally { borrowed.Active = false; frame.Value = null; }
        }
        private async Task InvokeUnauthorized(HttpResponseMessage response, string? rejected)
        {
            Task? original = null; var borrowed = new Frame(); frame.Value = borrowed;
            try { original = auth.OnUnauthorized!(new(response, endpoint, rejected), source.Token).AsTask(); await original.ConfigureAwait(false); }
            catch (OperationCanceledException error) when (original is { IsCanceled: true } && source.IsCancellationRequested && error.CancellationToken == source.Token) { throw; }
            catch (Exception error) { throw Capture("unauthorized", original, error); }
            finally { borrowed.Active = false; frame.Value = null; }
        }
        private async Task<HttpResponseMessage> SendCoreAsync()
        {
            await admission.Task.ConfigureAwait(false);
            byte[]? body = null;
            if (template.Content is not null)
            {
                if (template.Content.Headers.ContentLength is not { } declared || declared < 0 || declared > maximumBytes)
                    throw new InvalidOperationException("Authentication requires an admitted finite buffered request body.");
                body = await Invoke("content", () => new ValueTask<byte[]>(template.Content.ReadAsByteArrayAsync(source.Token))).ConfigureAwait(false);
                if (body.Length > maximumBytes) throw new InvalidOperationException("Authenticated request body exceeds admitted bound.");
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                source.Token.ThrowIfCancellationRequested();
                var token = await Invoke("token", () => auth.Token(source.Token)).ConfigureAwait(false);
                if (token is not null && (Encoding.UTF8.GetByteCount(token) > maximumBytes || token.Contains('\r') || token.Contains('\n')))
                    throw new ArgumentException("Admitted bearer token exceeds finite header shape.");
                HttpRequestMessage? request = null; HttpResponseMessage? response = null;
                var transferred = false; var errors = new List<Exception>();
                IMcpAdmittedHttpRequestOperation? lease = null;
                try
                {
                    request = new(template.Method, endpoint) { Version = template.Version, VersionPolicy = template.VersionPolicy };
                    foreach (var option in template.Options) request.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
                    foreach (var header in template.Headers)
                        if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value)) throw new ArgumentException("Unsupported captured request header.");
                    if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    if (body is not null)
                    {
                        request.Content = new ByteArrayContent(body);
                        foreach (var header in template.Content!.Headers)
                            if (!request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value)) throw new ArgumentException("Unsupported captured content header.");
                    }
                    lock (gate)
                    {
                        source.Token.ThrowIfCancellationRequested();
                        if (stop is not null) throw new OperationCanceledException(source.Token);
                        var borrowed = new Frame(); frame.Value = borrowed;
                        try { lease = physical(request) ?? throw new InvalidOperationException("Physical factory returned no admitted lease."); }
                        catch (Exception error) { throw Capture("physical-factory", null, error); }
                        finally { borrowed.Active = false; frame.Value = null; }
                        leases.Add(lease);
                    }
                    var admittedLease = lease ?? throw new InvalidOperationException("No owned physical lease after admission.");
                    response = await Invoke("physical-send", () => admittedLease.SendAsync(source.Token)).ConfigureAwait(false);
                    if (response is null) throw new InvalidOperationException("Physical send returned no response owner.");
                    // Late success remains a transferred response; transport owns disposal after cancellation.
                    if (source.IsCancellationRequested || attempt > 0 || auth.OnUnauthorized is null ||
                        template.Method != HttpMethod.Get && template.Method != HttpMethod.Post || !NeedsAuthorization(response))
                    { transferred = true; }
                    else await InvokeUnauthorized(response, token).ConfigureAwait(false);
                }
                catch (Exception error) { errors.Add(error); }
                finally
                {
                    if (!transferred && response is not null)
                        try { response.Dispose(); } catch (Exception error) { errors.Add(Capture("discard-response", null, error)); }
                    if (request is not null)
                        try { request.Dispose(); } catch (Exception error) { errors.Add(Capture("dispose-request", null, error)); }
                }
                if (errors.Count > 0)
                {
                    // A request-disposal failure prevents the intended response transfer. Retain it
                    // and dispose the still-owned response rather than leaking a success owner.
                    if (transferred && response is not null)
                        try { response.Dispose(); } catch (Exception error) { errors.Add(Capture("discard-response", null, error)); }
                    Throw(errors);
                }
                if (transferred) return response!;
                if (lease is not null)
                {
                    await GetPhysicalStop(lease).ConfigureAwait(false);
                }
                // Reject response has been discarded before a second token callback or physical send.
            }
            throw new InvalidOperationException("Unreachable authenticated attempt bound.");
        }
        private bool NeedsAuthorization(HttpResponseMessage response)
        {
            long headerBytes = 0;
            foreach (var header in response.Headers)
            {
                headerBytes += Encoding.UTF8.GetByteCount(header.Key);
                foreach (var value in header.Value) headerBytes += Encoding.UTF8.GetByteCount(value);
                if (headerBytes > maximumBytes) throw new InvalidOperationException("Authentication response headers exceed admitted bound.");
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized) return true;
            if (response.StatusCode != HttpStatusCode.Forbidden) return false;
            var challenge = response.Headers.TryGetValues("WWW-Authenticate", out var values) ? string.Join(",", values) : "";
            if (Encoding.UTF8.GetByteCount(challenge) > maximumBytes) throw new InvalidOperationException("Authentication challenge exceeds admitted bound.");
            return Regex.IsMatch(challenge, "(?:^|[\\s,])error=\"?insufficient_scope\"?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        private async Task StopCoreAsync(CancellationFrame? logicalParent, CancellationFrame? synchronousParent)
        {
            var errors = new List<Exception>(); Task? cancellation = null;
            // The captured owned Task executes real synchronous cancellation under TLS. CancelAsync
            // would queue callbacks on an unguarded thread after normal Register restores old EC.
            try { cancellation = Task.Run(() => CancelOwned(logicalParent, synchronousParent)); }
            catch (Exception error) { errors.Add(Capture("cancel", null, error)); }
            await admission.Task.ConfigureAwait(false);
            IMcpAdmittedHttpRequestOperation[] owned; Task<HttpResponseMessage>? originalSend;
            lock (gate) { owned = leases.ToArray(); originalSend = send; }
            // Physical aborts begin before joining callback/send originals. No new lease may enter after fencing.
            var stopping = new List<Task>();
            foreach (var lease in owned)
                try { stopping.Add(GetPhysicalStop(lease)); } catch (Exception error) { errors.Add(Capture("physical-stop", null, error)); }
            foreach (var original in stopping)
                try { await original.ConfigureAwait(false); } catch (Exception error) { errors.Add(Capture("physical-stop", original, error)); }
            if (originalSend is not null) try { await originalSend.ConfigureAwait(false); } catch { /* Send owns its full failure inventory. */ }
            if (cancellation is not null)
                try { await cancellation.ConfigureAwait(false); } catch (Exception error) { errors.Add(Capture("cancel", cancellation, error)); }
            try { callerRegistration.Dispose(); } catch (Exception error) { errors.Add(Capture("caller-registration", null, error)); }
            try { source.Dispose(); } catch (Exception error) { errors.Add(Capture("owned-token", null, error)); }
            Throw(errors);
        }
        private void CancelOwned(CancellationFrame? logicalParent, CancellationFrame? synchronousParent)
        {
            var previousLogical = cancellationContext.Value; var previousSynchronous = synchronousCancellation;
            var owned = new CancellationFrame(this, logicalParent, synchronousParent);
            cancellationContext.Value = owned; synchronousCancellation = owned;
            try { source.Cancel(); }
            finally
            {
                owned.Active = false;
                synchronousCancellation = previousSynchronous; cancellationContext.Value = previousLogical;
            }
        }
        private Task GetPhysicalStop(IMcpAdmittedHttpRequestOperation lease)
        {
            lock (gate)
            {
                if (physicalStops.TryGetValue(lease, out var retained)) return retained;
                var original = JoinPhysicalStopAsync(lease); physicalStops.Add(lease, original); return original;
            }
        }
        private async Task JoinPhysicalStopAsync(IMcpAdmittedHttpRequestOperation lease)
        {
            await Task.Yield(); // Publish ownership before any borrowed stop callback can run.
            var borrowed = new Frame(); frame.Value = borrowed; Task? original = null;
            try { original = lease.StopAsync(); await original.ConfigureAwait(false); }
            catch (Exception error) { throw Capture("physical-stop", original, error); }
            finally { borrowed.Active = false; frame.Value = null; }
        }
        private static void Throw(List<Exception> errors)
        {
            if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
            if (errors.Count > 1) throw new AggregateException("Authenticated MCP request retained multiple originals.", errors);
        }
    }
}
