// PiSharp-only (decision 0010, M6). `pisharp durable attach`: a line-mode terminal on one agent of a device's session, through
// DurableDeviceClient like every other client; nothing reads the device's files. The agent's event stream is followed across reconnects
// (FollowAsync: each connection starts with hello and a fresh snapshot, so nothing is replayed) and reduced by the client's AgentModel, the
// same reducer the hub uses. AttachView prints each transcript item once it is final, and each tool call when it starts and when it ends;
// what was printed is remembered by key, so the snapshot after a reconnect prints nothing twice. A typed line is a message with its own
// request ID: a send that fails keeps the ID, /retry sends it again with the same ID (the device cannot submit it twice), and a reconnect
// never resends anything. Question forms, plan reviews and job approvals print as text with the command that answers them; a command for a
// feature the device does not offer says so instead of failing.
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;
using PiSharp.Durable.Client;

namespace PiSharp.Cli.Durable;

/// <summary>What <c>attach</c> prints from one agent's stream.</summary>
internal sealed class AttachView(TextWriter output)
{
    /// <summary>How many of the newest transcript items the first snapshot prints; /history shows the rest.</summary>
    internal const int Recent = 20;

    private readonly AgentModel model = new();
    private readonly HashSet<string> printed = new(StringComparer.Ordinal);
    private bool started;
    private bool quiet;

    /// <summary>The agent's state.</summary>
    public AgentModel Model => model;

    /// <summary>How many connections have started (hello frames).</summary>
    public int Connections { get; private set; }

    /// <summary>Applies a frame and prints what became final.</summary>
    public void Apply(SseFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Type == "hello")
        {
            if (++Connections > 1) output.WriteLine("[reconnected]");
            return;
        }
        if (model.Apply(frame.Data) && model.Ready) Flush();
    }

    private void Flush()
    {
        var items = model.Items;
        var earlier = started ? 0 : Math.Max(0, items.Count - Recent);
        if (earlier > 0) output.WriteLine($"[{earlier} earlier items; /history shows the transcript]");
        started = true;
        for (var index = 0; index < items.Count; index++)
        {
            quiet = index < earlier;
            switch (items[index])
            {
                case UserItem user:
                    Print(user.Key, "> " + user.Text + Images(user.Images.Count));
                    break;
                case AssistantItem assistant:
                    // A streaming message's text and tool arguments are partial: it prints once committed.
                    if (assistant.Streaming || assistant.Id is null) break;
                    var text = string.Join("\n", assistant.Blocks.OfType<TextBlockView>().Select(block => block.Text)).Trim();
                    if (text.Length > 0) Print(assistant.Key, text);
                    if (assistant.Error is { } error) Print(assistant.Key + ":error", "[error] " + error);
                    foreach (var block in assistant.Blocks)
                        if (block is ToolBlockView tool) Call(tool.Call);
                    break;
                case ToolItem tool:
                    Call(tool.Call);
                    break;
                case ToolResultItem result:
                    Print(result.Key, $"  {(result.IsError ? "✗" : "⎿")} {result.Name}: {Line(result.Text, 200)}");
                    break;
                case EventItem note:
                    Print(note.Key, $"[{note.EntryKind}] {Line(note.Text, 200)}" + (note.Note is { } extra ? " — " + extra : ""));
                    break;
            }
        }
        quiet = false;
        foreach (var pending in model.Pending) Print("pending:" + pending.TaskId.ToString(CultureInfo.InvariantCulture), Card(pending));
    }

    private void Call(ToolCallView call)
    {
        Print("call:" + call.CallId, $"• {call.Name} {Line(Transcript.Describe(call.Args), 120)}".TrimEnd());
        if (call.Status is ToolStatus.Done or ToolStatus.Error && call.Result is { } result)
            Print("done:" + call.CallId, $"  {(call.Status == ToolStatus.Error ? "✗" : "⎿")} {Line(result, 200)}" + Images(call.Images.Count));
        foreach (var nested in call.Nested) Call(nested);
    }

    private void Print(string key, string text)
    {
        if (printed.Add(key) && !quiet) output.WriteLine(text);
    }

    /// <summary>A waiting question form or plan review as text, with the command that answers it.</summary>
    internal static string Card(PendingCall pending)
    {
        var task = pending.TaskId.ToString(CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        if (pending.IsPlan)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"[plan {task}] The agent asks you to review its plan:");
            foreach (var line in (Str(pending.Details, "plan") ?? "").Split('\n').Take(40)) text.AppendLine("  " + line);
            text.Append(CultureInfo.InvariantCulture, $"  Answer: /plan {task} approve [feedback] | /plan {task} reject [feedback]");
            return text.ToString();
        }
        var questions = pending.Details["questions"] as JsonArray ?? [];
        text.AppendLine(CultureInfo.InvariantCulture, $"[question {task}] The agent asks:");
        var index = 0;
        foreach (var question in questions.OfType<JsonObject>())
        {
            index++;
            var many = question["multiSelect"]?.GetValue<bool>() == true ? " (choose any)" : "";
            text.AppendLine(CultureInfo.InvariantCulture, $"  {index}. {Str(question, "question")}{many}");
            var option = 0;
            foreach (var choice in (question["options"] as JsonArray ?? []).OfType<JsonObject>())
                text.AppendLine(CultureInfo.InvariantCulture, $"     {++option}) {Str(choice, "label")} — {Str(choice, "description")}");
        }
        text.Append(CultureInfo.InvariantCulture, $"  Answer: /answer {task} <choice numbers or your own words>[; <next question>] | /decline {task}");
        return text.ToString();
    }

    private static string Images(int count) => count == 0 ? "" : $" [{count} image{(count == 1 ? "" : "s")}]";

    internal static string? Str(JsonObject json, string key) => json[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    internal static string Line(string text, int length)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= length ? line : line[..(length - 1)] + "…";
    }
}

/// <summary><c>pisharp durable attach</c>.</summary>
internal sealed class DurableAttach(DurableDeviceClient client, DeviceInfo device, string session, long agentId, TextWriter output)
{
    internal const string Usage = "[--session <session>] [--agent <id>]";

    internal const string Help = """
        Type a message to send it (it queues while the agent works). Commands:
          /steer <text>          send a message that steers the current work
          /retry                 send the last unsent message again, with the same request ID
          /abort                 stop the agent's current work
          /cancel <task>         cancel one task, such as an execution
          /answer <task> <a>[; <b>...]   answer a question form: choice numbers (1,2) or your own words, one part per question
          /decline <task>        decline a question form
          /plan <task> approve|reject [feedback]   review a plan
          /approve <task>, /deny <task>   decide a job run waiting for approval
          /pending               what waits for you: questions, plan reviews and job approvals
          /model <provider/id>, /thinking <level>   change the agent's model or thinking level
          /history [lines]       the transcript as Markdown (default the last 40 lines)
          /artifacts             the session's newest artifacts
          /status                the agent's model, state and queue
          /quit                  detach (the agent keeps working)
        """;

    private (string Text, string RequestId, string WhenBusy)? unsent;

    /// <summary>What has been printed from the stream.</summary>
    public AttachView View { get; } = new(output);

    /// <summary>The message a failed send kept, with its request ID.</summary>
    public (string Text, string RequestId, string WhenBusy)? Unsent => unsent;

    /// <summary>Follows the agent's stream until cancelled; disconnects print a line and reconnect.</summary>
    public async Task FollowAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in client.FollowAsync(DurableDeviceClient.AgentEventsPath(session, agentId),
                reason => output.WriteLine($"[disconnected: {reason} Reconnecting.]"), cancellationToken).ConfigureAwait(false))
                View.Apply(frame);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { /* Detached. */ }
        catch (DurableApiException error)
        {
            output.WriteLine($"[stream ended: {error.Code}: {error.Message}]");
        }
    }

    /// <summary>Handles one input line; false to detach.</summary>
    public async Task<bool> HandleAsync(string line, CancellationToken cancellationToken)
    {
        line = line.Trim();
        if (line.Length == 0) return true;
        if (!line.StartsWith('/')) return await SendAsync(line, "followUp", null, cancellationToken).ConfigureAwait(false);
        var space = line.IndexOf(' ', StringComparison.Ordinal);
        var name = space < 0 ? line : line[..space];
        var rest = space < 0 ? "" : line[(space + 1)..].Trim();
        try
        {
            switch (name)
            {
                case "/quit" or "/exit":
                    return false;
                case "/help":
                    output.WriteLine(Help);
                    break;
                case "/steer" when rest.Length > 0:
                    return await SendAsync(rest, "steer", null, cancellationToken).ConfigureAwait(false);
                case "/retry":
                    if (unsent is not { } kept) output.WriteLine("Nothing to retry.");
                    else return await SendAsync(kept.Text, kept.WhenBusy, kept.RequestId, cancellationToken).ConfigureAwait(false);
                    break;
                case "/abort":
                    await client.AbortAgentAsync(session, agentId, cancellationToken: cancellationToken).ConfigureAwait(false);
                    output.WriteLine("Stopping the agent's work.");
                    break;
                case "/cancel" when ParseTask(rest) is { } task:
                    var cancelled = await client.AbortTaskAsync(session, task, cancellationToken).ConfigureAwait(false);
                    output.WriteLine($"Task {task}: {cancelled?["status"]}.");
                    break;
                case "/answer" when Split(rest) is ({ } task, { Length: > 0 } answers):
                    await AnswerAsync(task, answers, cancellationToken).ConfigureAwait(false);
                    break;
                case "/decline" when ParseTask(rest) is { } task:
                    await client.AnswerInteractionAsync(session, agentId, task, new JsonObject { ["decline"] = true }, cancellationToken).ConfigureAwait(false);
                    output.WriteLine($"Declined {task}.");
                    break;
                case "/plan" when Split(rest) is ({ } task, { Length: > 0 } verdict):
                    await PlanAsync(task, verdict, cancellationToken).ConfigureAwait(false);
                    break;
                case "/approve" or "/deny" when ParseTask(rest) is { } task:
                    await client.DecideJobAsync(session, agentId, task, name == "/approve", cancellationToken).ConfigureAwait(false);
                    output.WriteLine($"Job run {task}: {(name == "/approve" ? "approved" : "declined")}.");
                    break;
                case "/pending":
                    await PendingAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "/model" when rest.IndexOf('/', StringComparison.Ordinal) is var slash and > 0 && slash < rest.Length - 1:
                    await client.ConfigureAgentAsync(session, agentId, new JsonObject
                    {
                        ["model"] = new JsonObject { ["provider"] = rest[..slash], ["modelId"] = rest[(slash + 1)..] },
                    }, cancellationToken).ConfigureAwait(false);
                    output.WriteLine($"Model: {rest}.");
                    break;
                case "/thinking" when rest.Length > 0:
                    await client.ConfigureAgentAsync(session, agentId, new JsonObject { ["thinkingLevel"] = rest }, cancellationToken).ConfigureAwait(false);
                    output.WriteLine($"Thinking: {rest}.");
                    break;
                case "/history":
                    var lines = int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0 ? count : 40;
                    var markdown = (await client.TranscriptAsync(session, agentId, markdown: true, cancellationToken).ConfigureAwait(false))?["text"]?.GetValue<string>() ?? "";
                    output.WriteLine(string.Join('\n', markdown.Split('\n').TakeLast(lines)));
                    break;
                case "/artifacts":
                    if (!device.Supports("artifacts"))
                    {
                        output.WriteLine($"{device.Name} does not offer artifacts.");
                        break;
                    }
                    var artifacts = await client.ArtifactsAsync(session, 10, cancellationToken: cancellationToken).ConfigureAwait(false);
                    output.WriteLine(DurableRemote.Table(artifacts?["items"] as JsonArray ?? [], ["id", "kind", "title"]));
                    break;
                case "/status":
                    Status();
                    break;
                default:
                    output.WriteLine($"Unknown or incomplete command: {line}. /help lists the commands.");
                    break;
            }
        }
        catch (DurableApiException error)
        {
            output.WriteLine($"{error.Code}: {error.Message}");
        }
        catch (HttpRequestException error)
        {
            output.WriteLine($"offline: {error.Message}");
        }
        return true;
    }

    /// <summary>Sends a message. A failure keeps it and its request ID for /retry; a message sent while another is kept replaces it.</summary>
    private async Task<bool> SendAsync(string text, string whenBusy, string? requestId, CancellationToken cancellationToken)
    {
        requestId ??= "attach-" + Guid.NewGuid().ToString("n");
        var busy = View.Model.Running;
        try
        {
            await client.SendMessageAsync(session, agentId, text, requestId, whenBusy, cancellationToken).ConfigureAwait(false);
            unsent = null;
            if (busy && whenBusy == "followUp") output.WriteLine("[queued until the agent is free]");
        }
        catch (Exception error) when (error is DurableApiException or HttpRequestException)
        {
            unsent = (text, requestId, whenBusy);
            var code = error is DurableApiException api ? api.Code : DurableErrorCodes.Offline;
            output.WriteLine($"Not confirmed ({code}: {error.Message}). /retry sends it again with request ID {requestId}, so it cannot be submitted twice.");
        }
        return true;
    }

    private async Task AnswerAsync(long task, string text, CancellationToken cancellationToken)
    {
        var pending = View.Model.Pending.FirstOrDefault(call => call.TaskId == task && !call.IsPlan);
        var questions = (pending?.Details["questions"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var parts = text.Split(';', StringSplitOptions.TrimEntries);
        var answers = new JsonArray();
        for (var index = 0; index < Math.Max(parts.Length, questions.Count); index++)
        {
            var part = index < parts.Length ? parts[index] : "";
            var options = index < questions.Count ? (questions[index]["options"] as JsonArray ?? []).OfType<JsonObject>().ToList() : [];
            var numbers = part.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var chosen = numbers.Length > 0 && numbers.All(number => int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= options.Count)
                ? numbers.Select(number => AttachView.Str(options[int.Parse(number, CultureInfo.InvariantCulture) - 1], "label")).ToList() : null;
            var answer = new JsonObject { ["selected"] = new JsonArray([.. (chosen ?? []).Select(label => (JsonNode?)label)]) };
            if (chosen is null && part.Length > 0) answer["other"] = part;
            answers.Add(answer);
        }
        await client.AnswerInteractionAsync(session, agentId, task, new JsonObject { ["answers"] = answers }, cancellationToken).ConfigureAwait(false);
        output.WriteLine($"Answered {task}.");
    }

    private async Task PlanAsync(long task, string verdict, CancellationToken cancellationToken)
    {
        var space = verdict.IndexOf(' ', StringComparison.Ordinal);
        var choice = space < 0 ? verdict : verdict[..space];
        var feedback = space < 0 ? null : verdict[(space + 1)..].Trim();
        if (choice is not ("approve" or "reject"))
        {
            output.WriteLine("Use /plan <task> approve|reject [feedback].");
            return;
        }
        var body = new JsonObject { ["approved"] = choice == "approve" };
        if (!string.IsNullOrEmpty(feedback)) body["feedback"] = feedback;
        await client.AnswerInteractionAsync(session, agentId, task, body, cancellationToken).ConfigureAwait(false);
        output.WriteLine($"Plan {task}: {choice}.");
    }

    private async Task PendingAsync(CancellationToken cancellationToken)
    {
        var any = false;
        foreach (var call in View.Model.Pending)
        {
            output.WriteLine(AttachView.Card(call));
            any = true;
        }
        var jobs = await client.JobsAsync(session, agentId, cancellationToken).ConfigureAwait(false);
        foreach (var run in (jobs?["runs"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (run["approval"]?["status"]?.GetValue<string>() != "pending") continue;
            output.WriteLine($"[job {run["id"]}] {run["title"]} waits for approval in phase {run["phase"]}. /approve {run["id"]} | /deny {run["id"]}");
            any = true;
        }
        if (!any) output.WriteLine("Nothing waits for you.");
    }

    private void Status()
    {
        var agent = View.Model.Agent;
        output.WriteLine($"Session {session}, agent {agentId}: {(View.Model.Running ? "working" : "idle")}; model {agent["model"]?["provider"]}/{agent["model"]?["modelId"] ?? agent["model"]?["id"]}, " +
            $"thinking {agent["thinkingLevel"]}; {View.Model.Inbox.Count} queued; about {View.Model.ContextTokens:0} context tokens" +
            (unsent is { } kept ? $"; unsent: \"{AttachView.Line(kept.Text, 40)}\" (/retry)" : ""));
    }

    private static long? ParseTask(string text) => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var task) ? task : null;

    private static (long? Task, string Text) Split(string text)
    {
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (ParseTask(text), "") : (ParseTask(text[..space]), text[(space + 1)..].Trim());
    }

    /// <summary>Runs <c>pisharp durable attach</c>: connects, picks the session and agent, then follows the stream and reads lines.</summary>
    internal static async Task<int> RunAsync(string[] args, PiHost host, CancellationToken cancellationToken)
    {
        var output = TextWriter.Synchronized(host.Stdout);
        try
        {
            var parsed = RemoteArgs.Parse(args, UsageShape.Parse(Usage));
            var (url, token) = DurableRemote.Connect(parsed, host);
            using var client = new DurableDeviceClient(url, token);
            var device = await client.DeviceAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var session = parsed.Option("--session");
            if (session is null)
            {
                var sessions = (await client.SessionsAsync(cancellationToken).ConfigureAwait(false)) as JsonArray ?? [];
                if (sessions.Count != 1)
                {
                    await host.Stderr.WriteLineAsync((sessions.Count == 0 ? "The device has no sessions." : "Choose a session with --session:\n" +
                        DurableRemote.Table(sessions, ["id", "name", "cwd"])) + $"\nUsage: pisharp durable attach {Usage}").ConfigureAwait(false);
                    return DurableExit.Usage;
                }
                session = sessions[0]!["id"]!.GetValue<string>();
            }
            var agents = (await client.AgentsAsync(session, cancellationToken).ConfigureAwait(false)) as JsonArray ?? [];
            var agentId = parsed.Long("--agent") ?? agents.OfType<JsonObject>().FirstOrDefault(agent => agent["kind"]?.GetValue<string>() == "root")?["id"]?.GetValue<long>()
                ?? throw new DurableApiException(DurableErrorCodes.NotFound, $"Session {session} has no main agent.");
            var attach = new DurableAttach(client, device, session, agentId, output);
            output.WriteLine($"Attached to {device.Name}, session {session}, agent {agentId}. /help lists the commands; /quit detaches.");
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var follow = attach.FollowAsync(stop.Token);
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var line = await host.Stdin.ReadLineAsync(stop.Token).ConfigureAwait(false);
                    if (line is null || !await attach.HandleAsync(line, stop.Token).ConfigureAwait(false)) break;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { /* Detaching. */ }
            await stop.CancelAsync().ConfigureAwait(false);
            await follow.ConfigureAwait(false);
            if (attach.Unsent is { } kept)
                output.WriteLine($"Detached with an unsent message (request ID {kept.RequestId}); it was not submitted unless the device confirmed it.");
            return DurableExit.Ok;
        }
        catch (RemoteUsageException usage)
        {
            await host.Stderr.WriteLineAsync($"{usage.Message}\nUsage: pisharp durable attach {Usage}").ConfigureAwait(false);
            return DurableExit.Usage;
        }
        catch (DurableApiException error)
        {
            await host.Stderr.WriteLineAsync($"{error.Code}: {error.Message}").ConfigureAwait(false);
            return DurableExit.Of(error.Code);
        }
        catch (HttpRequestException error)
        {
            await host.Stderr.WriteLineAsync($"offline: {error.Message}").ConfigureAwait(false);
            return DurableExit.Offline;
        }
    }
}
