using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PiSharp.Durable.Hosting;

namespace PiSharp.Durable.Server;

public static partial class DurableDeviceApi
{
    private sealed class ScheduleTriggerEndpoint;

    private static void MapAutomation(RouteGroupBuilder v1, DurableDeviceHost host, DurableDeviceServerOptions options)
    {
        v1.MapGet("/schedules", () => Json(new JsonArray([.. host.Schedules().Select(item => DeviceService.ToNode(item.ToJson()))])));
        v1.MapPost("/schedules", H(async http =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            if (body["input"] is not (null or JsonObject)) throw new HostInvalidException("input must be a JSON object.");
            var created = await host.CreateScheduleAsync(String(body, "sessionId") ?? throw new HostInvalidException("sessionId is required."),
                (long)(Integer(body, "agentId", 1, 9_007_199_254_740_991) ?? 1), String(body, "jobId") ?? throw new HostInvalidException("jobId is required."),
                (int)(Integer(body, "everyMinutes", 1, 525600) ?? throw new HostInvalidException("everyMinutes is required.")),
                body["input"] is JsonObject input ? (Chord.JsonObjectValue)DeviceService.ToChord(input) : null, http.RequestAborted).ConfigureAwait(false);
            return Json(DeviceService.ToNode(created.ToJson()), 201);
        }));
        v1.MapPatch("/schedules/{id}", async (HttpContext http, string id) =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var interval = Integer(body, "everyMinutes", 1, 525600);
            var updated = await host.UpdateScheduleAsync(id, Bool(body, "enabled"), interval is { } value ? (int)value : null, http.RequestAborted).ConfigureAwait(false);
            return Json(DeviceService.ToNode(updated.ToJson()));
        });
        v1.MapDelete("/schedules/{id}", async (HttpContext http, string id) =>
        { await host.DeleteScheduleAsync(id, http.RequestAborted).ConfigureAwait(false); return Json(new { ok = true }); });
        v1.MapPost("/schedules/{id}/tick", async (HttpContext http, string id) =>
            Json(DeviceService.ToNode((await host.TickScheduleAsync(id, http.RequestAborted).ConfigureAwait(false)).ToJson())))
            .WithMetadata(new ScheduleTriggerEndpoint());

        v1.MapGet("/trigger-credentials", () => Json(new JsonArray([.. host.TriggerCredentials().Select(item => DeviceService.ToNode(item.ToJson()))])));
        v1.MapPost("/trigger-credentials", H(async http =>
        {
            var body = await BodyAsync(http, options).ConfigureAwait(false);
            var issued = await host.IssueTriggerCredentialAsync(String(body, "label") ?? "Automation", String(body, "scheduleId"),
                (int)(Integer(body, "expiresInHours", 1, 8760) ?? 720), http.RequestAborted).ConfigureAwait(false);
            http.Response.Headers.CacheControl = "no-store";
            return Json(new JsonObject { ["credential"] = DeviceService.ToNode(issued.Credential.ToJson()), ["token"] = issued.Token }, 201);
        }));
        v1.MapDelete("/trigger-credentials/{id}", (string id) => { host.RevokeTriggerCredential(id); return Json(new { ok = true }); });

        v1.MapGet("/sessions/{s}/executions", async (HttpContext http, string s) =>
        {
            var query = http.Request.Query;
            Chord.JsonObjectValue? cursor = null;
            if (query["cursor"].ToString() is { Length: > 0 } raw)
            {
                if (raw.Length > 2048) throw new HostInvalidException("The cursor is too long.");
                cursor = Chord.JsonValue.Parse(raw) as Chord.JsonObjectValue ?? throw new HostInvalidException("Invalid history cursor.");
            }
            var limit = query["limit"].ToString() is { Length: > 0 } count ? Id(count, "limit") : 20;
            if (limit is < 1 or > 100) throw new HostInvalidException("limit must be 1 to 100.");
            var result = await host.ResolveSession(s).ExecutionsAsync(query["kind"], query["status"], query["q"], (int)limit, cursor, http.RequestAborted).ConfigureAwait(false);
            return Json(DeviceService.ToNode(result));
        });
        v1.MapGet("/sessions/{s}/executions/{taskId}", async (HttpContext http, string s, string taskId) =>
            Json(DeviceService.ToNode(await host.ResolveSession(s).ExecutionAsync(Id(taskId, "taskId"), http.RequestAborted).ConfigureAwait(false))));
    }
}
