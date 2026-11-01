// PiSharp-only (decision 0010, M6): typed conveniences for the v1 session, agent, task and trigger-credential routes that clients reached
// through SendAsync before, so the CLI's remote commands and `pisharp durable attach` use the same calls as every other client. Each returns
// the device's JSON unchanged; GetAsync and SendAsync stay the escape hatches.
using System.Text.Json.Nodes;

namespace PiSharp.Durable.Client;

public sealed partial class DurableDeviceClient
{
    /// <summary>The device's sessions with their agents (<c>GET /v1/sessions</c>).</summary>
    public Task<JsonNode?> SessionsAsync(CancellationToken cancellationToken = default) => GetAsync("/v1/sessions", cancellationToken: cancellationToken);

    /// <summary>One session (<c>GET /v1/sessions/{s}</c>); <paramref name="session"/> is an ID or an unambiguous name.</summary>
    public Task<JsonNode?> SessionAsync(string session, CancellationToken cancellationToken = default) =>
        GetAsync(SessionPath(session), cancellationToken: cancellationToken);

    /// <summary>Creates a session from <c>{name, cwd, model?}</c>; the same Idempotency-Key and body replay the first session.</summary>
    public Task<JsonNode?> CreateSessionAsync(JsonObject session, string? idempotencyKey = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "/v1/sessions", session, idempotencyKey, cancellationToken: cancellationToken);

    /// <summary>A session's agents (<c>GET /v1/sessions/{s}/agents</c>).</summary>
    public Task<JsonNode?> AgentsAsync(string session, CancellationToken cancellationToken = default) =>
        GetAsync(SessionPath(session) + "/agents", cancellationToken: cancellationToken);

    /// <summary>
    /// Sends a message (<c>POST .../agents/{a}/messages</c>). <paramref name="requestId"/> identifies the submission: retrying a failed send
    /// with the same ID can never submit it twice. <paramref name="whenBusy"/> is <c>followUp</c>, <c>steer</c> or <c>reject</c>.
    /// </summary>
    public Task<JsonNode?> SendMessageAsync(string session, long agentId, string content, string requestId, string whenBusy = "followUp",
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, AgentPath(session, agentId) + "/messages",
            new JsonObject { ["content"] = content, ["requestId"] = requestId, ["whenBusy"] = whenBusy }, cancellationToken: cancellationToken);

    /// <summary>Stops an agent's current work (<c>POST .../agents/{a}/abort</c>).</summary>
    public Task<JsonNode?> AbortAgentAsync(string session, long agentId, bool background = false, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, AgentPath(session, agentId) + "/abort", new JsonObject { ["background"] = background }, cancellationToken: cancellationToken);

    /// <summary>Cancels one task, such as an execution (<c>POST /v1/sessions/{s}/tasks/{taskId}/abort</c>): <c>{status: terminal|marked}</c>.</summary>
    public Task<JsonNode?> AbortTaskAsync(string session, long taskId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"{SessionPath(session)}/tasks/{Number(taskId)}/abort", new JsonObject(), cancellationToken: cancellationToken);

    /// <summary>An agent's transcript (<c>GET .../agents/{a}/transcript</c>): items, or Markdown with <paramref name="markdown"/>.</summary>
    public Task<JsonNode?> TranscriptAsync(string session, long agentId, bool markdown = false, CancellationToken cancellationToken = default) =>
        GetAsync(AgentPath(session, agentId) + "/transcript" + (markdown ? "?format=markdown" : ""), cancellationToken: cancellationToken);

    /// <summary>An agent's model, thinking level, instructions and working directory (<c>GET .../agents/{a}/config</c>).</summary>
    public Task<JsonNode?> AgentConfigAsync(string session, long agentId, CancellationToken cancellationToken = default) =>
        GetAsync(AgentPath(session, agentId) + "/config", cancellationToken: cancellationToken);

    /// <summary>Changes <c>{model?: {provider, modelId}, thinkingLevel?, instructions?, cwd?, readOnly?}</c> (<c>PATCH .../config</c>).</summary>
    public Task<JsonNode?> ConfigureAgentAsync(string session, long agentId, JsonObject change, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Patch, AgentPath(session, agentId) + "/config", change, cancellationToken: cancellationToken);

    /// <summary>Tool calls waiting for a person: question forms and plan reviews (<c>GET .../agents/{a}/interactions</c>).</summary>
    public Task<JsonNode?> InteractionsAsync(string session, long agentId, CancellationToken cancellationToken = default) =>
        GetAsync(AgentPath(session, agentId) + "/interactions", cancellationToken: cancellationToken);

    /// <summary>Answers a waiting call (<c>POST .../interactions/{taskId}</c>): <c>{answers: [{selected, other?}]}</c>, <c>{decline: true}</c>
    /// or, for a plan review, <c>{approved, feedback?}</c>.</summary>
    public Task<JsonNode?> AnswerInteractionAsync(string session, long agentId, long taskId, JsonObject answer, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"{AgentPath(session, agentId)}/interactions/{Number(taskId)}", answer, cancellationToken: cancellationToken);

    /// <summary>An agent's job definitions and runs (<c>GET .../agents/{a}/jobs</c>).</summary>
    public Task<JsonNode?> JobsAsync(string session, long agentId, CancellationToken cancellationToken = default) =>
        GetAsync(AgentPath(session, agentId) + "/jobs", cancellationToken: cancellationToken);

    /// <summary>Approves or declines a job run waiting in an approval phase (<c>POST .../jobs/{taskId}/approval</c>).</summary>
    public Task<JsonNode?> DecideJobAsync(string session, long agentId, long taskId, bool approved, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"{AgentPath(session, agentId)}/jobs/{Number(taskId)}/approval", new JsonObject { ["approved"] = approved },
            cancellationToken: cancellationToken);

    /// <summary>Trigger credentials, without their tokens (<c>GET /v1/trigger-credentials</c>).</summary>
    public Task<JsonNode?> TriggerCredentialsAsync(CancellationToken cancellationToken = default) =>
        GetAsync("/v1/trigger-credentials", cancellationToken: cancellationToken);

    /// <summary>Issues a trigger credential (<c>POST /v1/trigger-credentials</c>): <c>{credential, token}</c>. The token is shown only here.</summary>
    public Task<JsonNode?> IssueTriggerCredentialAsync(string label, string? scheduleId = null, int expiresInHours = 720, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "/v1/trigger-credentials",
            new JsonObject { ["label"] = label, ["scheduleId"] = scheduleId, ["expiresInHours"] = expiresInHours }, cancellationToken: cancellationToken);

    /// <summary>Revokes a trigger credential (<c>DELETE /v1/trigger-credentials/{id}</c>).</summary>
    public Task<JsonNode?> RevokeTriggerCredentialAsync(string id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"/v1/trigger-credentials/{Uri.EscapeDataString(id)}", cancellationToken: cancellationToken);

    /// <summary>The path of an agent's event stream, for <see cref="StreamAsync"/> and <see cref="FollowAsync"/>.</summary>
    public static string AgentEventsPath(string session, long agentId) => AgentPath(session, agentId) + "/events";

    private static string SessionPath(string session) => "/v1/sessions/" + Uri.EscapeDataString(session);

    private static string AgentPath(string session, long agentId) => $"{SessionPath(session)}/agents/{Number(agentId)}";
}
