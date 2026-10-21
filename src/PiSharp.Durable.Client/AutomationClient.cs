using System.Text.Json.Nodes;

namespace PiSharp.Durable.Client;

public sealed partial class DurableDeviceClient
{
    /// <summary>Lists recurring schedules. Requires the administrative device credential.</summary>
    public Task<JsonNode?> SchedulesAsync(CancellationToken cancellationToken = default) => GetAsync("/v1/schedules", cancellationToken: cancellationToken);

    /// <summary>Creates a schedule from {sessionId, agentId?, jobId, everyMinutes, input?}.</summary>
    public Task<JsonNode?> CreateScheduleAsync(JsonObject schedule, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "/v1/schedules", schedule, cancellationToken: cancellationToken);

    /// <summary>Updates {enabled?, everyMinutes?}; an interval change starts a new clock.</summary>
    public Task<JsonNode?> UpdateScheduleAsync(string id, JsonObject change, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Patch, $"/v1/schedules/{Uri.EscapeDataString(id)}", change, cancellationToken: cancellationToken);

    /// <summary>Deletes a schedule and revokes tokens restricted to it. Admitted jobs continue.</summary>
    public Task<JsonNode?> DeleteScheduleAsync(string id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"/v1/schedules/{Uri.EscapeDataString(id)}", cancellationToken: cancellationToken);

    /// <summary>Ticks a due schedule. Also works with a trigger-only credential; paused, early and overlapping runs are not forced.</summary>
    public Task<JsonNode?> TickScheduleAsync(string id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"/v1/schedules/{Uri.EscapeDataString(id)}/tick", new JsonObject(), cancellationToken: cancellationToken);

    /// <summary>Lists executions, filtered before paging. Pass the prior response's next object as cursor.</summary>
    public Task<JsonNode?> ExecutionsAsync(string session, string? kind = null, string? status = null, string? query = null, int limit = 20,
        JsonObject? cursor = null, CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string> { ["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (kind is not null) values["kind"] = kind;
        if (status is not null) values["status"] = status;
        if (query is not null) values["q"] = query;
        if (cursor is not null) values["cursor"] = cursor.ToJsonString();
        return GetAsync($"/v1/sessions/{Uri.EscapeDataString(session)}/executions?" +
            string.Join('&', values.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))), cancellationToken: cancellationToken);
    }

    /// <summary>An execution's tasks, entries, job audit and recorded usage.</summary>
    public Task<JsonNode?> ExecutionAsync(string session, long taskId, CancellationToken cancellationToken = default) =>
        GetAsync($"/v1/sessions/{Uri.EscapeDataString(session)}/executions/{taskId.ToString(System.Globalization.CultureInfo.InvariantCulture)}", cancellationToken: cancellationToken);
}
