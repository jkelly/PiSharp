// PiSharp-only (decision 0008, PLAN.md §7 Routes and views). The hub's own JSON API: the network, device checks, UI state, and editing the
// registry (PiSharp's devices page: add, edit, enter a token, forget an identity, remove, import dpi-network.yaml). A token is checked
// against the device before it is stored. Errors use the device API's envelope.
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using PiSharp.Durable.Client;
using PiSharp.Durable.Web.Hub;

namespace PiSharp.Durable.Web.Controllers;

[ApiController]
[Route("api")]
public sealed class HubApiController(DeviceRegistry registry, DeviceMonitor monitor, HubOptions options) : ControllerBase
{
    public sealed record DeviceBody(string? Id, string? Name, string? Url, string? Token);

    public sealed record ImportBody(string? Yaml, bool Apply);

    public sealed record UiStateBody(JsonNode? Value);

    [HttpGet("network")]
    public IActionResult Network() => Ok(new { configFile = options.ConfigFile, devices = monitor.Network() });

    [HttpPost("devices/{id}/check")]
    public async Task<IActionResult> Check(string id)
    {
        if (registry.Get(id) is null) return Error(404, DurableErrorCodes.NotFound, $"No device {id}.");
        monitor.Check(id);
        return Ok(await monitor.SettledAsync(id, TimeSpan.FromSeconds(8)));
    }

    [HttpGet("ui-state/{key}")]
    public IActionResult GetUiState(string key) => Ok(new { value = registry.UiGet(key) is { } stored ? JsonNode.Parse(stored) : null });

    [HttpPut("ui-state/{key}")]
    public IActionResult SetUiState(string key, [FromBody] UiStateBody body)
    {
        if (key.Length > 100) return Error(400, DurableErrorCodes.Invalid, "The key is too long.");
        var json = body.Value?.ToJsonString();
        if (json is { Length: > 16384 }) return Error(400, DurableErrorCodes.Invalid, "The value is too large.");
        registry.UiSet(key, json);
        return Ok(new { ok = true });
    }

    /// <summary>Checks a new device's address and token, then adds it.</summary>
    [HttpPost("devices")]
    public async Task<IActionResult> Add([FromBody] DeviceBody body, CancellationToken cancellationToken)
    {
        if (!DeviceRegistry.IsValidId(body.Id)) return Error(400, DurableErrorCodes.Invalid, "The name must be lower-case letters, digits and dashes, at most 32.");
        if (registry.Get(body.Id!) is not null) return Error(409, DurableErrorCodes.Conflict, $"A device called {body.Id} already exists.");
        string url;
        try { url = DeviceRegistry.NormalizeUrl(body.Url); }
        catch (ArgumentException invalid) { return Error(400, DurableErrorCodes.Invalid, invalid.Message); }
        if (body.Token is not { Length: >= 32 } token) return Error(400, DurableErrorCodes.Invalid, "The token has at least 32 characters. Run pisharp durable token on the device.");
        var (info, failure) = await ProbeAsync(url, token, cancellationToken);
        if (info is null) return Error(failure!.Status is > 0 ? failure.Status : 502, failure.Code, failure.Message);
        registry.Upsert(body.Id!, body.Name ?? body.Id!, "url", url, "ui", token);
        registry.LearnUid(body.Id!, info.Uid);
        return Ok(new { device = await monitor.SettledAsync(body.Id!, TimeSpan.FromSeconds(8)), info, plainHttp = url.StartsWith("http://", StringComparison.Ordinal) && !IsLoopback(url) });
    }

    /// <summary>Edits a device's address (UI devices) or token (any device); a new token is checked first.</summary>
    [HttpPatch("devices/{id}")]
    public async Task<IActionResult> Edit(string id, [FromBody] DeviceBody body, CancellationToken cancellationToken)
    {
        var device = registry.Get(id);
        if (device is null) return Error(404, DurableErrorCodes.NotFound, $"No device {id}.");
        string? url = null;
        if (!string.IsNullOrWhiteSpace(body.Url) && body.Url != device.Url)
        {
            if (device.Source != "ui") return Error(400, DurableErrorCodes.Invalid, $"{id} comes from {DeviceRegistry.SourceText(device.Source)}; edit it there.");
            try { url = DeviceRegistry.NormalizeUrl(body.Url); }
            catch (ArgumentException invalid) { return Error(400, DurableErrorCodes.Invalid, invalid.Message); }
        }
        if (!string.IsNullOrEmpty(body.Token))
        {
            if (body.Token.Length < 32) return Error(400, DurableErrorCodes.Invalid, "The token has at least 32 characters.");
            var (info, failure) = await ProbeAsync(url ?? device.Url, body.Token, cancellationToken);
            if (info is null) return Error(failure!.Status is > 0 ? failure.Status : 502, failure.Code, failure.Message);
        }
        registry.Update(id, url, string.IsNullOrEmpty(body.Token) ? null : body.Token);
        monitor.Check(id);
        return Ok(new { device = await monitor.SettledAsync(id, TimeSpan.FromSeconds(8)) });
    }

    [HttpDelete("devices/{id}")]
    public IActionResult Remove(string id)
    {
        try
        {
            registry.Remove(id);
            return Ok(new { ok = true });
        }
        catch (KeyNotFoundException missing) { return Error(404, DurableErrorCodes.NotFound, missing.Message); }
        catch (InvalidOperationException readOnly) { return Error(400, DurableErrorCodes.Invalid, readOnly.Message); }
    }

    [HttpPost("devices/{id}/forget-identity")]
    public IActionResult ForgetIdentity(string id)
    {
        if (registry.Get(id) is null) return Error(404, DurableErrorCodes.NotFound, $"No device {id}.");
        registry.ForgetUid(id);
        monitor.Check(id);
        return Ok(new { ok = true });
    }

    /// <summary>Previews or applies a dpi-network.yaml: its devices are added read-only, without tokens.</summary>
    [HttpPost("devices/import")]
    public IActionResult Import([FromBody] ImportBody body)
    {
        IReadOnlyList<NetworkFileDevice> declared;
        try { declared = NetworkFile.Parse(body.Yaml ?? ""); }
        catch (FormatException invalid) { return Error(400, DurableErrorCodes.Invalid, invalid.Message); }
        var rows = declared.Select(device =>
        {
            var existing = registry.Get(device.Id);
            var result = existing is null ? "add" : existing.Source == "config" ? existing.Url == device.Url ? "same" : "update" : "conflict";
            return new { device.Id, device.Name, device.Kind, device.Url, result };
        }).ToList();
        if (body.Apply)
            foreach (var row in rows.Where(row => row.result is "add" or "update")) registry.Upsert(row.Id, row.Name, row.Kind, row.Url, "config", null);
        return Ok(new { devices = rows, applied = body.Apply });
    }

    private static async Task<(DeviceInfo? Info, DurableApiException? Failure)> ProbeAsync(string url, string token, CancellationToken cancellationToken)
    {
        using var client = DeviceClients.For(url, token);
        try
        {
            var info = await client.DeviceAsync(TimeSpan.FromSeconds(6), cancellationToken);
            return info.ApiVersion == DurableWire.ApiVersion ? (info, null)
                : (null, new DurableApiException("incompatible", $"The device speaks API version {info.ApiVersion}; this hub speaks version {DurableWire.ApiVersion}.", 400));
        }
        catch (DurableApiException failure)
        {
            return (null, failure.Code == DurableErrorCodes.Unauthorized
                ? new DurableApiException(failure.Code, "The device rejected the token. Run pisharp durable token on the device and paste what it prints.", 401)
                : failure);
        }
    }

    private static bool IsLoopback(string url) => Uri.TryCreate(url, UriKind.Absolute, out var parsed) && (parsed.IsLoopback || parsed.Host == "localhost");

    private ObjectResult Error(int status, string code, string message) => StatusCode(status, new ApiErrorBody(new ApiError(code, message)));
}
