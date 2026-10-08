using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Text;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Transport;

namespace PiSharp.Cli.Mcp.Authentication;

internal sealed class McpDefaultOAuthHttp(McpDefaultOAuthHost host, McpDefaultOAuthHostResources resources)
{
    internal async Task<McpOAuthExchangeResponse> SendAsync(Uri endpoint, McpDefaultOAuthHttpPurpose purpose,
        HttpMethod method, ImmutableDictionary<string, string> headers, string? body, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https"))
            throw new ArgumentException("Absolute proposed HTTP endpoint required.");
        // Admission is checked BEFORE acquiring a physical factory/request original.
        if (!host.InvokeBorrowed(() => resources.AdmitEndpoint(endpoint, purpose)))
            throw new InvalidOperationException("OAuth endpoint/purpose was not admitted by its caller.");
        long requestBytes = Encoding.UTF8.GetByteCount(endpoint.AbsoluteUri) + Encoding.UTF8.GetByteCount(body ?? "");
        foreach (var pair in headers) requestBytes += Encoding.UTF8.GetByteCount(pair.Key) + (long)Encoding.UTF8.GetByteCount(pair.Value);
        if (requestBytes > resources.MaximumBytes) throw new McpOAuthProtocolException("request_bound", "OAuth request exceeds its finite admission.");
        HttpRequestMessage? request = null; HttpResponseMessage? response = null;
        IMcpAdmittedHttpRequestOperation? operation = null; Stream? stream = null;
        CancellationTokenRegistration registration = default;
        var stopGate = new object(); Task? stopOriginal = null; Exception? stopInitiation = null;
        Task StopOriginal()
        {
            lock (stopGate)
            {
                if (stopOriginal is not null) return stopOriginal;
                if (stopInitiation is not null) ExceptionDispatchInfo.Capture(stopInitiation).Throw();
                try { return stopOriginal = host.InvokeBorrowed(() => operation!.StopAsync()) ?? throw new InvalidOperationException("Physical OAuth stop returned no original."); }
                catch (Exception error) { stopInitiation = error; throw; }
            }
        }
        McpOAuthExchangeResponse? result = null; var errors = new List<Exception>();
        try
        {
            request = new(method, endpoint);
            if (body is not null) request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            foreach (var pair in headers)
            {
                if (pair.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    if (request.Content is null) throw new ArgumentException("Content header requires a body.");
                    request.Content.Headers.Add(pair.Key, pair.Value);
                }
                else request.Headers.Add(pair.Key, pair.Value);
            }
            token.ThrowIfCancellationRequested();
            operation = host.InvokeBorrowed(() => resources.Http(request)) ?? throw new InvalidOperationException("No admitted physical OAuth request owner.");
            // Cancellation starts the actual physical stop before joining an uncooperative send.
            // The same raw Stop Task is then directly joined in finally. Initiation faults remain retained.
            registration = token.Register(() => { try { _ = StopOriginal(); } catch { /* finally retains the exact initiation error */ } });
            response = await host.ObserveAsync("http:" + purpose + ":send", () => operation.SendAsync(token), token,
                acquired => response = acquired).ConfigureAwait(false);
            if (response is null || (int)response.StatusCode is < 100 or > 599)
                throw new McpOAuthProtocolException("invalid_exchange", "OAuth HTTP response envelope is invalid.");
            if (response.Content.Headers.ContentLength is { } declared && (declared < 0 || declared > resources.MaximumBytes))
                throw new McpOAuthProtocolException("response_bound", "Declared OAuth response exceeds its finite admission.");
            stream = await host.ObserveAsync("http:" + purpose + ":body-open",
                () => new ValueTask<Stream>(response.Content.ReadAsStreamAsync(token)), token,
                acquired => stream = acquired).ConfigureAwait(false);
            using var bytes = new MemoryStream(); var buffer = new byte[8192];
            while (true)
            {
                var read = await host.ObserveAsync("http:" + purpose + ":body-read",
                    () => stream.ReadAsync(buffer.AsMemory(), token), token).ConfigureAwait(false);
                if (read == 0) break;
                if (bytes.Length + read > resources.MaximumBytes)
                    throw new McpOAuthProtocolException("response_bound", "OAuth response exceeds its finite admission.");
                bytes.Write(buffer, 0, read);
            }
            // Fetch Response.text uses replacement decoding rather than an ambient encoding.
            result = new((int)response.StatusCode, Encoding.UTF8.GetString(bytes.ToArray()));
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            try { registration.Dispose(); } catch (Exception error) { errors.Add(error); }
            if (operation is not null)
                try { await host.ObserveAsync("http:" + purpose + ":stop", () => new ValueTask(StopOriginal()), CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) { errors.Add(error); }
            if (stream is not null) try { host.ObserveDisposal("http:" + purpose + ":stream-dispose", stream.Dispose); } catch (Exception error) { errors.Add(error); }
            if (response is not null) try { host.ObserveDisposal("http:" + purpose + ":response-dispose", response.Dispose); } catch (Exception error) { errors.Add(error); }
            if (request is not null) try { host.ObserveDisposal("http:" + purpose + ":request-dispose", request.Dispose); } catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new McpDefaultOAuthHostFailure("http:" + purpose, host.CapturedOriginals,
            new AggregateException("OAuth HTTP send/body/stop/disposal originals retained.", errors));
        return result ?? throw new InvalidOperationException("OAuth exchange produced no response.");
    }
}
