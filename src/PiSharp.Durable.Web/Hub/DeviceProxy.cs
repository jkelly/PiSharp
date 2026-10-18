// PiSharp-only (decision 0008). The browser's device requests, after durable-pi's hub proxy (apps/dpi-playground/apps/dpi-playground-hub/
// src/proxy.ts, MIT): `/api/devices/{id}/v1/{path}` goes to the device with its token added, so the browser never holds one. Streams are
// refused here (the per-tab stream carries them), requests time out after 330 s (a send may wait up to 300 s), and `Idempotency-Key` goes
// up while `Idempotent-Replay` comes back. An unreachable device answers 502 with the `offline` code.
using System.Net.Http.Headers;
using PiSharp.Durable.Client;

namespace PiSharp.Durable.Web.Hub;

/// <summary>Forwards REST calls to devices.</summary>
public sealed class DeviceProxy(DeviceRegistry registry, DeviceMonitor monitor, HubOptions options, IHttpClientFactory clients)
{
    /// <summary>The name of the proxy's HTTP client.</summary>
    public const string ClientName = "device-proxy";

    public async Task HandleAsync(HttpContext http, string id, string path)
    {
        var device = registry.Get(id);
        if (device is null)
        {
            await WriteErrorAsync(http, 404, DurableErrorCodes.NotFound, $"No device {id}.").ConfigureAwait(false);
            return;
        }
        if (path.EndsWith("/events", StringComparison.Ordinal) || path == "events")
        {
            await WriteErrorAsync(http, 400, DurableErrorCodes.Invalid, "Streams go through /api/stream.").ConfigureAwait(false);
            return;
        }
        var token = registry.Token(id);
        if (token is null)
        {
            await WriteErrorAsync(http, 401, DurableErrorCodes.Unauthorized, $"No token for {id}. Enter it on the Devices page.").ConfigureAwait(false);
            return;
        }
        var target = new Uri(new Uri(device.Url), "/v1/" + path + http.Request.QueryString.Value);
        using var request = new HttpRequestMessage(new HttpMethod(http.Request.Method), target);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (http.Request.Headers.TryGetValue("Idempotency-Key", out var key)) request.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString());
        if (http.Request.ContentLength > 0 || http.Request.Headers.TransferEncoding.Count > 0)
        {
            var body = new MemoryStream();
            await http.Request.Body.CopyToAsync(body, http.RequestAborted).ConfigureAwait(false);
            body.Position = 0;
            request.Content = new StreamContent(body);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(http.Request.ContentType ?? "application/json");
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        limit.CancelAfter(options.ProxyTimeout);
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is HttpRequestException or OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
        {
            if (failure is HttpRequestException) monitor.Check(id);
            await WriteErrorAsync(http, 502, DurableErrorCodes.Offline, failure is OperationCanceledException
                ? $"{id} did not answer within {options.ProxyTimeout.TotalSeconds:0} s." : $"{id} is unreachable: {failure.Message}").ConfigureAwait(false);
            return;
        }
        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) monitor.Check(id);
            http.Response.StatusCode = (int)response.StatusCode;
            http.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            if (response.Headers.TryGetValues("Idempotent-Replay", out var replay)) http.Response.Headers["Idempotent-Replay"] = replay.First();
            await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted).ConfigureAwait(false);
        }
    }

    public static Task WriteErrorAsync(HttpContext http, int status, string code, string message)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json; charset=utf-8";
        return http.Response.WriteAsync(DurableWire.Serialize(new ApiErrorBody(new ApiError(code, message))), http.RequestAborted);
    }
}
