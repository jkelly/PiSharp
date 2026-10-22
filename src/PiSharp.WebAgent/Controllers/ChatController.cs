// Decision 0011 (run W2, WU7): the chat pages and the chat commands. Pages: / (to a new chat), /chat/new, /chat/{id} (the stored transcript of
// the chat's current branch, or ?agent=N, which the page's stream then catches up) and /chat/{id}/export.md. Commands are JSON POSTs under
// /api/chats (HostGuard refuses other content types and cross-origin writes); each is safe to repeat: a create, send, fork, edit or compact
// carries a requestId and a retry with it changes nothing new, and the rest set a state. A command that needs the chat idle (compact, model
// change) answers 409 {error: {code: "busy"}} while it runs; a second decision on an approval answers 409 "already_decided". Errors are
// {error: {code, message}}. No answer carries a credential: a chat names its account by label only. WU8 adds /chat/{id}/tree (the session
// tree modal's body), /api/chats/{id}/retry (Retry and Regenerate fork and send again; a retry with the same requestId makes nothing new)
// and /api/chats/{id}/compaction (the automatic compaction switch).
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Controllers;

/// <summary>The chat pages and the <c>/api/chats</c> commands.</summary>
public sealed class ChatController(ChatService chats, ApprovalBroker broker, DeviceHostService device) : Controller
{
    // ─── Pages ──────────────────────────────────────────────────────────────

    /// <summary>The app's home: a new chat.</summary>
    [HttpGet("/")]
    public IActionResult Home() => Redirect("/chat/new");

    /// <summary>The new chat page.</summary>
    [HttpGet("/chat")]
    [HttpGet("/chat/new")]
    public IActionResult New() => View("Index", chats.NewChat());

    /// <summary>A chat page: the stored transcript of the chat's current branch (or <paramref name="agent"/>).</summary>
    [HttpGet("/chat/{id}")]
    public async Task<IActionResult> Session(string id, long? agent, CancellationToken cancellationToken)
    {
        try
        {
            return View("Session", await chats.OpenAsync(id, agent, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception error) when (error is ChatException { Status: 404 } || ChatErrors.From(error) is { Status: 404 })
        {
            Response.StatusCode = 404;
            return View("Missing", id);
        }
    }

    /// <summary>The session tree modal's body (<c>_ChatTree</c>): every branch's entries, for forking from any message and switching branch.</summary>
    [HttpGet("/chat/{id}/tree")]
    public async Task<IActionResult> Tree(string id, long? agent, CancellationToken cancellationToken)
    {
        try
        {
            return PartialView("~/Views/Chat/_ChatTree.cshtml", await chats.TreeAsync(id, agent, cancellationToken).ConfigureAwait(false));
        }
        catch (ChatException error)
        {
            return Error(error.Status, error.Code, error.Message);
        }
        catch (Exception error) when (ChatErrors.From(error) is { } mapped)
        {
            return Error(mapped.Status, mapped.Code, mapped.Message);
        }
    }

    /// <summary>A branch's transcript as Markdown.</summary>
    [HttpGet("/chat/{id}/export.md")]
    public Task<IActionResult> Export(string id, long? agent, CancellationToken cancellationToken) => Run(async () =>
    {
        var markdown = await chats.ExportMarkdownAsync(id, agent, cancellationToken).ConfigureAwait(false);
        var name = new string([.. (chats.Find(id)?.Title ?? "chat").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')]).Trim('-');
        return File(Encoding.UTF8.GetBytes(markdown), "text/markdown; charset=utf-8", (name.Length == 0 ? "chat" : name) + ".md");
    });

    // ─── Chats ──────────────────────────────────────────────────────────────

    /// <summary><c>POST /api/chats {requestId, text, provider?, model?, thinking?, tier?, cwd?, title?}</c>: creates a chat and sends its first
    /// message. Answers <c>{id, url, title, agentId, submissionId, status}</c>.</summary>
    [HttpPost("/api/chats")]
    public Task<IActionResult> Create([FromBody] JsonObject? body, CancellationToken cancellationToken) => Run(async () =>
    {
        var created = await chats.CreateAsync(new ChatCreateRequest(Text(body, "requestId"), Text(body, "text"))
        {
            Provider = ChatDisplay.Str(body, "provider"),
            Model = ChatDisplay.Str(body, "model"),
            Thinking = ChatDisplay.Str(body, "thinking"),
            Tier = ChatDisplay.Str(body, "tier"),
            Cwd = ChatDisplay.Str(body, "cwd"),
            Title = ChatDisplay.Str(body, "title"),
        }, cancellationToken).ConfigureAwait(false);
        return Ok(new
        {
            id = created.Binding.SessionId,
            url = ChatUrl(created.Binding.SessionId),
            title = created.Binding.Title,
            agentId = 1,
            submissionId = created.SubmissionId,
            status = created.Status,
        });
    });

    /// <summary><c>GET /api/chats</c>: the chats, most recent first.</summary>
    [HttpGet("/api/chats")]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var list = await chats.ListAsync(int.MaxValue, cancellationToken).ConfigureAwait(false);
        return Ok(new { items = list.Select(chat => new { id = chat.Id, title = chat.Title, running = chat.Running, updated = chat.Updated }) });
    }

    /// <summary><c>GET /api/chats/{id}</c>: a chat's binding (its model, thinking level, tier, account label and current branch).</summary>
    [HttpGet("/api/chats/{id}")]
    public Task<IActionResult> Get(string id, CancellationToken cancellationToken) => Run(async () =>
    {
        var binding = chats.Require(id);
        return Ok(await DescribeAsync(binding, cancellationToken).ConfigureAwait(false));
    });

    /// <summary><c>POST /api/chats/{id}/messages {requestId, text, agentId?, steer?}</c>: sends (queues as a follow-up, or steers, while it
    /// runs). Answers <c>{agentId, submissionId, status}</c>.</summary>
    [HttpPost("/api/chats/{id}/messages")]
    public Task<IActionResult> Send(string id, [FromBody] JsonObject? body, CancellationToken cancellationToken) => Run(async () =>
    {
        var sent = await chats.SendAsync(id, new ChatSendRequest(Text(body, "requestId"), Text(body, "text"))
        {
            AgentId = ChatDisplay.Long(body, "agentId"),
            Steer = ChatDisplay.Bool(body, "steer"),
        }, cancellationToken).ConfigureAwait(false);
        return Ok(new { agentId = sent.AgentId, submissionId = sent.SubmissionId, status = sent.Status });
    });

    /// <summary><c>POST /api/chats/{id}/abort {agentId?}</c>: stops the run.</summary>
    [HttpPost("/api/chats/{id}/abort")]
    public Task<IActionResult> Abort(string id, [FromBody] JsonObject? body, CancellationToken cancellationToken) => Run(async () =>
    {
        await chats.AbortAsync(id, ChatDisplay.Long(body, "agentId"), cancellationToken).ConfigureAwait(false);
        return Ok(new { ok = true });
    });

    /// <summary><c>POST /api/chats/{id}/withdraw {submissionId, agentId?}</c>: withdraws a queued message.</summary>
    [HttpPost("/api/chats/{id}/withdraw")]
    public Task<IActionResult> Withdraw(string id, [FromBody] JsonObject? body, CancellationToken cancellationToken) => Run(async () =>
    {
        var submission = ChatDisplay.Long(body, "submissionId") ?? throw ChatException.Invalid("submissionId is required.");
        var withdrawn = await chats.WithdrawAsync(id, ChatDisplay.Long(body, "agentId"), submission, cancellationToken).ConfigureAwait(false);
        return Ok(new { withdrawn });
    });

    /// <summary><c>POST /api/chats/{id}/rename {title}</c>.</summary>
    [HttpPost("/api/chats/{id}/rename")]
    public Task<IActionResult> Rename(string id, [FromBody] JsonObject? body, CancellationToken cancellationToken) => Run(async () =>
    {
        var binding = await chats.RenameAsync(id, Text(body, "title"), cancellationToken).ConfigureAwait(false);
        return Ok(new { title = binding.Title });
    });

    /// <summary><c>POST /api/chats/{id}/archive {}</c>: deletes the chat (its session moves to the device's archive).</summary>
    [HttpPost("/api/chats/{id}/archive")]
    public Task<IActionResult> Archive(string id, CancellationToken cancellationToken) => Run(async () =>
    {
        await chats.ArchiveAsync(id, cancellationToken).ConfigureAwait(false);
        return Ok(new { ok = true });
    });

    /// <summary><c>POST /api/chats/{id}/fork {requestId, entryId, agentId?}</c>: forks the branch at an entry. Answers
    /// <c>{agentId, name, url}</c>.</summary>
    [HttpPost("/api/chats/{id}/fork")]
    public Task<IActionResult> Fork(string id, [FromBody] JsonObject? body, CancellationToken cancellationToken) => Run(async () =>
    {
        var entry = ChatDisplay.Long(body, "entryId") ?? throw ChatException.Invalid("entryId is required.");
        var branch = await chats.ForkAsync(id, ChatDisplay.Long(body, "agentId"), entry, Text(body, "requestId"), cancellationToken).ConfigureAwait(false);
        return Ok(new { agentId = branch.AgentId, name = branch.Name, url = ChatUrl(id, branch.AgentId) });
    });

    /// <summary><c>POST /api/chats/{id}/edit {requestId, entryId, text, agentId?}</c>: edit-and-resend on a new branch. Answers
    /// <c>{agentId, name, url}</c>.</summary>
    [HttpPost("/api/chats/{id}/edit")]
    public Task<IActionResult> Edit(string id, [FromBody] JsonObject? body, CancellationToken cancellationToken) => Run(async () =>
    {
        var entry = ChatDisplay.Long(body, "entryId") ?? throw ChatException.Invalid("entryId is required.");
        var branch = await chats.EditAsync(id, ChatDisplay.Long(body, "agentId"), entry, Text(body, "text"), Text(body, "requestId"), cancellationToken)
            .ConfigureAwait(false);
        return Ok(new { agentId = branch.AgentId, name = branch.Name, url = ChatUrl(id, branch.AgentId) });
    });

    /// <summary><c>POST /api/chats/{id}/retry {requestId, entryId, agentId?}</c>: Retry or Regenerate an answer: a new branch from just before
    /// the message it replies to, which is sent again with the chat's own model and account. Answers <c>{agentId, name, url}</c>.</summary>
    [HttpPost("/api/chats/{id}/retry")]
    public Task<IActionResult> Retry(string id, [FromBody] JsonObject? body, CancellationToken cancellationToken) => Run(async () =>
    {
        var entry = ChatDisplay.Long(body, "entryId") ?? throw ChatException.Invalid("entryId is required.");
        var branch = await chats.RetryAsync(id, ChatDisplay.Long(body, "agentId"), entry, Text(body, "requestId"), cancellationToken).ConfigureAwait(false);
        return Ok(new { agentId = branch.AgentId, name = branch.Name, url = ChatUrl(id, branch.AgentId) });
    });

    /// <summary><c>POST /api/chats/{id}/compaction {enabled}</c>: turns automatic compaction on or off for the chat. Answers
    /// <c>{enabled, reserveTokens, keepRecentTokens}</c>.</summary>
    [HttpPost("/api/chats/{id}/compaction")]
    public Task<IActionResult> Compaction(string id, [FromBody] JsonObject? body) => Run(() =>
    {
        if (body?["enabled"] is not JsonValue value || !value.TryGetValue<bool>(out var enabled)) throw ChatException.Invalid("enabled is required.");
        var compaction = chats.SetAutoCompaction(id, enabled);
        return Task.FromResult<IActionResult>(Ok(new { enabled = compaction.Enabled, reserveTokens = compaction.ReserveTokens, keepRecentTokens = compaction.KeepRecentTokens }));
    });

    /// <summary><c>POST /api/chats/{id}/compact {requestId, agentId?, instructions?}</c>: 409 while the chat runs. Answers <c>{taskId}</c>.</summary>
    [HttpPost("/api/chats/{id}/compact")]
    public Task<IActionResult> Compact(string id, [FromBody] JsonObject? body, CancellationToken cancellationToken) => Run(async () =>
    {
        var task = await chats.CompactAsync(id, ChatDisplay.Long(body, "agentId"), ChatDisplay.Str(body, "instructions"), Text(body, "requestId"),
            cancellationToken).ConfigureAwait(false);
        return Ok(new { taskId = task });
    });

    /// <summary><c>POST /api/chats/{id}/model {provider?, model?, thinking?, tier?, accountId?}</c>: 409 while the chat runs. Answers the
    /// chat as <c>GET /api/chats/{id}</c> does.</summary>
    [HttpPost("/api/chats/{id}/model")]
    public Task<IActionResult> ChangeModel(string id, [FromBody] JsonObject? body, CancellationToken cancellationToken) => Run(async () =>
    {
        var binding = await chats.ChangeModelAsync(id, new ChatModelChange
        {
            Provider = ChatDisplay.Str(body, "provider"),
            Model = ChatDisplay.Str(body, "model"),
            Thinking = ChatDisplay.Str(body, "thinking"),
            Tier = body?.ContainsKey("tier") == true ? ChatDisplay.Str(body, "tier") ?? "" : null,
            AccountId = ChatDisplay.Str(body, "accountId"),
        }, cancellationToken).ConfigureAwait(false);
        return Ok(await DescribeAsync(binding, cancellationToken).ConfigureAwait(false));
    });

    // ─── Waiting tool calls ─────────────────────────────────────────────────

    /// <summary><c>GET /api/chats/{id}/approvals</c>: the tool calls waiting for a decision.</summary>
    [HttpGet("/api/chats/{id}/approvals")]
    public Task<IActionResult> Approvals(string id) => Run(() =>
    {
        chats.Require(id);
        return Task.FromResult<IActionResult>(Ok(new
        {
            items = broker.Pending(id).Select(approval => new
            {
                taskId = approval.Request.TaskId,
                agentId = approval.Request.AgentId,
                callId = approval.Request.CallId,
                tool = approval.Request.Tool,
                command = approval.Request.Command,
                arguments = approval.Request.Arguments,
                since = approval.Since,
            }),
        }));
    });

    /// <summary><c>POST /api/chats/{id}/approvals/{taskId} {decision: allow|deny|allowChat|allowAlways, reason?}</c>: the first decision
    /// wins; a later one answers 409 <c>already_decided</c> with the decision that won.</summary>
    [HttpPost("/api/chats/{id}/approvals/{taskId:long}")]
    public Task<IActionResult> Decide(string id, long taskId, [FromBody] JsonObject? body, CancellationToken cancellationToken) => Run(async () =>
    {
        var choice = ApprovalDecision.Parse(ChatDisplay.Str(body, "decision"))
            ?? throw ChatException.Invalid("decision must be one of " + string.Join(", ", ApprovalDecision.Names.Values) + ".");
        var reason = ChatDisplay.Str(body, "reason");
        if (reason is { Length: > 2000 }) throw ChatException.Invalid("A reason must have at most 2000 characters.");
        var outcome = await chats.DecideApprovalAsync(id, taskId, new ApprovalDecision(choice, reason), cancellationToken).ConfigureAwait(false);
        return outcome.Status switch
        {
            ApprovalOutcomeStatus.Decided => Ok(new { status = "decided", decision = outcome.Decision!.Name }),
            ApprovalOutcomeStatus.AlreadyDecided => Error(409, ChatErrors.AlreadyDecided, "This call was already decided" +
                (outcome.Decision is { } won ? $" ({won.Name})." : "."), new { decision = outcome.Decision?.Name }),
            _ => Error(404, ChatErrors.NotFound, "No tool call of this chat waits for that decision."),
        };
    });

    /// <summary><c>POST /api/chats/{id}/questions/{taskId} {answers: [{selected, other?}] | decline: true, agentId?}</c>.</summary>
    [HttpPost("/api/chats/{id}/questions/{taskId:long}")]
    public Task<IActionResult> Answer(string id, long taskId, [FromBody] JsonObject? body, CancellationToken cancellationToken) => Run(async () =>
    {
        await chats.AnswerQuestionsAsync(id, ChatDisplay.Long(body, "agentId"), taskId, ChatAnswers.Read(body ?? []), cancellationToken).ConfigureAwait(false);
        return Ok(new { ok = true });
    });

    /// <summary><c>POST /api/chats/{id}/plans/{taskId} {approved, feedback?, agentId?}</c>.</summary>
    [HttpPost("/api/chats/{id}/plans/{taskId:long}")]
    public Task<IActionResult> Review(string id, long taskId, [FromBody] JsonObject? body, CancellationToken cancellationToken) => Run(async () =>
    {
        if (body?["approved"] is not JsonValue approved || !approved.TryGetValue<bool>(out var yes)) throw ChatException.Invalid("approved is required.");
        await chats.ReviewPlanAsync(id, ChatDisplay.Long(body, "agentId"), taskId, yes, ChatDisplay.Str(body, "feedback"), cancellationToken)
            .ConfigureAwait(false);
        return Ok(new { ok = true });
    });

    /// <summary><c>GET /api/chats/{id}/grants</c>: what "Always allow in this chat" granted (until the app restarts).</summary>
    [HttpGet("/api/chats/{id}/grants")]
    public Task<IActionResult> Grants(string id) => Run(() =>
    {
        chats.Require(id);
        return Task.FromResult<IActionResult>(Ok(new { items = broker.Grants(id).Select(rule => new { tool = rule.Tool, command = rule.Command }) }));
    });

    /// <summary><c>POST /api/chats/{id}/grants/revoke {tool, command?}</c>.</summary>
    [HttpPost("/api/chats/{id}/grants/revoke")]
    public Task<IActionResult> Revoke(string id, [FromBody] JsonObject? body) => Run(() =>
    {
        chats.Require(id);
        var revoked = broker.Revoke(id, new ToolAllowRule(Text(body, "tool"), ChatDisplay.Str(body, "command")));
        return Task.FromResult<IActionResult>(Ok(new { revoked }));
    });

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static string ChatUrl(string chatId, long? agentId = null) =>
        "/chat/" + Uri.EscapeDataString(chatId) + (agentId is { } agent ? "?agent=" + agent : "");

    private static string Text(JsonObject? body, string key) =>
        ChatDisplay.Str(body, key) ?? throw ChatException.Invalid($"{key} is required.");

    private async Task<object> DescribeAsync(ChatBinding binding, CancellationToken cancellationToken) => new
    {
        id = binding.SessionId,
        title = binding.Title,
        provider = binding.Provider,
        model = binding.Model,
        thinking = binding.Thinking,
        tier = binding.Tier,
        account = await chats.AccountLabelAsync(binding, cancellationToken).ConfigureAwait(false),
        agentId = binding.AgentId,
        running = device.IsBusy(binding.SessionId),
        archived = binding.IsArchived,
        created = binding.Created,
        updated = binding.Updated,
    };

    private ObjectResult Error(int status, string code, string message, object? extra = null) =>
        StatusCode(status, extra is null
            ? new { error = new { code, message } }
            : new { error = new { code, message }, detail = extra });

    private async Task<IActionResult> Run(Func<Task<IActionResult>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (ChatException error)
        {
            return Error(error.Status, error.Code, error.Message);
        }
        catch (Exception error) when (ChatErrors.From(error) is { } mapped)
        {
            return Error(mapped.Status, mapped.Code, mapped.Message);
        }
    }
}
