// Decision 0012: messages between agents, the host and workflows. Every address has a mailbox (a session document family keyed by
// address); a message to an agent conversation is also submitted to it as input (an idle agent starts a turn, a busy one gets it as a
// follow-up, or a steer when urgent), with a request ID derived from the message ID, so a rerun never delivers twice. A message to an
// agent that is blocked waiting on the sender (an interactive delegate or wait_agent) goes to its mailbox only: the waiting call returns it.
// The model sees a header naming the sender; the authoritative sender is in the entry data. Messages are untrusted input.
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.CustomAgents.Workflows.Engine;

namespace PiSharp.CustomAgents.Teams;

/// <summary>What a message is.</summary>
public enum AgentMessageKind
{
    /// <summary>A note; no answer expected.</summary>
    Message,
    /// <summary>A question; answered with a reply.</summary>
    Question,
    /// <summary>The answer to a question.</summary>
    Reply,
    /// <summary>A subagent's answer to a delegated or spawned task.</summary>
    Result,
    /// <summary>A task given to a subagent.</summary>
    Task,
}

/// <summary>One message in a mailbox.</summary>
/// <param name="Id">The message ID (unique in the session).</param>
/// <param name="From">The sender's address.</param>
/// <param name="To">The recipient's address.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Text">The text.</param>
/// <param name="CorrelationId">For a reply: the question's ID; for a result: the task's request ID.</param>
/// <param name="At">When it was sent (Unix milliseconds).</param>
/// <param name="Acknowledged">Whether the recipient acknowledged it.</param>
public sealed record AgentMessage(string Id, string From, string To, AgentMessageKind Kind, string Text, string? CorrelationId, double At, bool Acknowledged);

/// <summary>Options of one send.</summary>
public sealed record AgentSendOptions
{
    /// <summary>Steer a busy agent (placed at its next step) instead of queueing a follow-up for after its current answer.</summary>
    public bool Urgent { get; init; }
    /// <summary>A message ID that makes the send idempotent; default a fresh one (deterministic inside tools and workflow actions).</summary>
    public string? Id { get; init; }
}

/// <summary>Sends and receives messages as one address.</summary>
public interface IAgentMessenger
{
    /// <summary>The address messages are sent from.</summary>
    string Self { get; }
    /// <summary>Sends <paramref name="text"/> to <paramref name="to"/> (an agent address, <c>parent</c>, or <c>host</c>).</summary>
    Task<AgentMessage> SendAsync(string to, string text, AgentSendOptions? options = null, CancellationToken cancellationToken = default);
    /// <summary>Answers the question <paramref name="messageId"/> from this address's mailbox.</summary>
    Task<AgentMessage> ReplyAsync(string messageId, string text, CancellationToken cancellationToken = default);
    /// <summary>Asks <paramref name="to"/> a question and waits for the reply or the agent's answer.</summary>
    Task<string> AskAsync(string to, string question, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
    /// <summary>The agents this address may message.</summary>
    Task<IReadOnlyList<AgentEntry>> ListAsync(CancellationToken cancellationToken = default);
    /// <summary>The messages in this address's mailbox, oldest first.</summary>
    Task<IReadOnlyList<AgentMessage>> ReadMailboxAsync(CancellationToken cancellationToken = default);
    /// <summary>Messages as they arrive (the ones already there first), until <paramref name="cancellationToken"/> is cancelled.</summary>
    IAsyncEnumerable<AgentMessage> WatchMailboxAsync(CancellationToken cancellationToken = default);
    /// <summary>Marks <paramref name="messageId"/> as handled.</summary>
    Task AcknowledgeAsync(string messageId, CancellationToken cancellationToken = default);
}

/// <summary>Messengers for the host, for tools and for workflow tasks.</summary>
public static class AgentMessaging
{
    /// <summary>The host's messenger (address <c>host</c>); the same as <see cref="AgentSession.Host"/>.</summary>
    public static IAgentMessenger ForHost(AgentSession session) => session.Host;

    /// <summary>A messenger for the agent calling a tool; sends are idempotent across reruns of the call.</summary>
    public static IAgentMessenger ForTool(IToolExecutionApi api, AgentTeam team) =>
        new Messenger(team, MessagingPort.ForTool(api, team), null);

    /// <summary>A messenger for a workflow task, at address <paramref name="self"/>; <paramref name="keyScope"/> makes its message IDs
    /// stable across reruns of one action visit.</summary>
    /// <summary>The address of a workflow run.</summary>
    public static string WorkflowAddress(TaskId run) => "workflow." + run.Value.ToString(CultureInfo.InvariantCulture);

    internal static IAgentMessenger ForTask(ITaskRuntime runtime, AgentTeam team, string self, string keyScope) =>
        new Messenger(team, MessagingPort.ForTask(runtime, team, keyScope), self);
}

/// <summary>The mailbox documents and message records.</summary>
internal static class Mailboxes
{
    public const int Limit = 200;

    /// <summary><c>{messages: [{id, from, to, kind, text, correlationId?, requestId?, at, ack?}]}</c>, keyed by address.</summary>
    public static SessionDocFamilyToken Doc { get; } = Documents.DefineSessionDocFamily("pisharp.agents.mailbox", 1,
        seed => seed as JsonObjectValue ?? JsonObjectValue.Empty);

    public static Task<DraftObject> OpenAsync(ITx tx, string address) => tx.DocAsync(Doc, address, JsonObjectValue.Empty);

    /// <summary>Creates the mailbox of <paramref name="address"/> when it is absent: a watch binds only an existing document.</summary>
    public static async Task EnsureAsync(ITx tx, string address)
    {
        var mailbox = await OpenAsync(tx, address).ConfigureAwait(false);
        if (mailbox.Get("messages") is not JsonArrayValue) mailbox.Set("messages", JsonValue.Array(Array.Empty<JsonValue>()));
    }

    /// <summary>Appends <paramref name="message"/> unless a message with its ID is there; returns false for a duplicate.</summary>
    public static bool Append(DraftObject mailbox, AgentMessage message, string? requestId = null, bool routed = false)
    {
        if (mailbox.Get("messages") is JsonArrayValue list && list.OfType<JsonObjectValue>().Any(item => Str(item, "id") == message.Id)) return false;
        RunDocs.Append(mailbox, "messages", RunDocs.Object(("id", message.Id), ("from", message.From), ("to", message.To),
            ("kind", message.Kind.ToString().ToLowerInvariant()), ("text", message.Text), ("correlationId", RunDocs.Opt(message.CorrelationId)),
            ("requestId", RunDocs.Opt(requestId)), ("routed", routed ? (JsonValue)true : null), ("at", message.At)), Limit);
        return true;
    }

    public static void Acknowledge(DraftObject mailbox, string id)
    {
        if (mailbox.Get("messages") is not JsonArrayValue) return;
        var list = mailbox.GetArray("messages");
        for (var index = 0; index < list.Count; index++)
            if (list.Get(index) is JsonObjectValue item && Str(item, "id") == id && !item.ContainsKey("ack"))
                list.Splice(index, 1, item.SetItem("ack", true));
    }

    public static IReadOnlyList<AgentMessage> Read(JsonObjectValue? mailbox) =>
        mailbox is not null && mailbox.TryGetValue("messages", out var list) && list is JsonArrayValue items
            ? [.. items.OfType<JsonObjectValue>().Select(ToMessage)]
            : [];

    public static AgentMessage? Find(JsonObjectValue? mailbox, string id) => Read(mailbox).FirstOrDefault(message => message.Id == id);

    /// <summary>Whether the message <paramref name="id"/> went to a waiting call instead of being submitted as input.</summary>
    public static bool Routed(JsonObjectValue? mailbox, string id) =>
        mailbox is not null && mailbox.TryGetValue("messages", out var list) && list is JsonArrayValue items &&
        items.OfType<JsonObjectValue>().Any(item => Str(item, "id") == id && item.TryGetValue("routed", out var routed) && routed is JsonBooleanValue { Value: true });

    public static string? RequestOf(JsonObjectValue? mailbox, string id) =>
        mailbox is not null && mailbox.TryGetValue("messages", out var list) && list is JsonArrayValue items
            ? items.OfType<JsonObjectValue>().Where(item => Str(item, "id") == id).Select(item => Str(item, "requestId")).FirstOrDefault()
            : null;

    private static AgentMessage ToMessage(JsonObjectValue item) => new(Str(item, "id") ?? "", Str(item, "from") ?? "", Str(item, "to") ?? "",
        Enum.TryParse<AgentMessageKind>(Str(item, "kind"), true, out var kind) ? kind : AgentMessageKind.Message, Str(item, "text") ?? "",
        Str(item, "correlationId"), item.TryGetValue("at", out var at) && at is JsonNumberValue number ? number.Value : 0,
        item.TryGetValue("ack", out var ack) && ack is JsonBooleanValue { Value: true });

    internal static string? Str(JsonObjectValue value, string key) => value.TryGetValue(key, out var item) && item is JsonStringValue text ? text.Value : null;

    /// <summary>The text a model sees for a message: a header naming the sender, then the escaped body.</summary>
    public static string Header(AgentMessage message) =>
        $"<agent-message from=\"{WebUtility.HtmlEncode(message.From)}\" id=\"{WebUtility.HtmlEncode(message.Id)}\" kind=\"{message.Kind.ToString().ToLowerInvariant()}\">\n" +
        WebUtility.HtmlEncode(message.Text) + "\n</agent-message>";

    /// <summary>The input entry data of a delivered message.</summary>
    public static JsonObjectValue EntryData(AgentMessage message) => RunDocs.Object(("from", message.From), ("to", message.To), ("id", message.Id),
        ("kind", message.Kind.ToString().ToLowerInvariant()), ("correlationId", RunDocs.Opt(message.CorrelationId)));

    public const string EntryKind = "agents.message";
}

/// <summary>The durable operations messaging needs, from the Harness (host), a tool call or a workflow task.</summary>
internal sealed class MessagingPort
{
    private int sequence;

    private MessagingPort(IDocumentReader reader, IDocumentObserver observer, Func<Func<ITx, Task<object?>>, DurableContext, Task<object?>> commit,
        Func<ConversationId, DurableContext, Task<IConversationHandleLike?>> conversation, DurableContext context, string? idScope, TimeProvider clock)
    {
        Reader = reader;
        Observer = observer;
        this.commit = commit;
        this.conversation = conversation;
        Context = context;
        IdScope = idScope;
        Clock = clock;
    }

    private readonly Func<Func<ITx, Task<object?>>, DurableContext, Task<object?>> commit;
    private readonly Func<ConversationId, DurableContext, Task<IConversationHandleLike?>> conversation;

    public IDocumentReader Reader { get; }
    public IDocumentObserver Observer { get; }
    public DurableContext Context { get; }
    /// <summary>Prefix of deterministic message IDs; null: fresh IDs.</summary>
    public string? IdScope { get; }
    public TimeProvider Clock { get; }
    /// <summary>The calling tool task, for tool ports.</summary>
    public TaskId? ToolTask { get; private init; }
    /// <summary>The calling conversation, for tool ports.</summary>
    public ConversationId? Caller { get; private init; }

    public static MessagingPort ForHost(IHarness harness, AgentTeam team) => new(harness, harness,
        async (change, context) => await harness.CommitAsync(change, context).ConfigureAwait(false),
        async (id, context) => await harness.ConversationAsync(id, context).ConfigureAwait(false) is { } found ? new Handle(found.SubmitAsync, found.AbortAsync) : null,
        DurableContext.Background, null, team.Options.TimeProvider ?? TimeProvider.System);

    public static MessagingPort ForTool(IToolExecutionApi api, AgentTeam team) => new(api, api,
        async (change, context) => await api.CommitAsync(change, context).ConfigureAwait(false),
        async (id, context) => await api.ConversationAsync(id, context).ConfigureAwait(false) is { } found ? new Handle(found.SubmitAsync, found.AbortAsync) : null,
        DurableContext.Background, "t" + api.TaskId.Value.ToString(CultureInfo.InvariantCulture), team.Options.TimeProvider ?? TimeProvider.System)
    {
        ToolTask = api.TaskId,
        Caller = api.ConversationId,
    };

    public static MessagingPort ForTask(ITaskRuntime runtime, AgentTeam team, string keyScope) => new(runtime, runtime,
        async (change, context) =>
        {
            object? result = null;
            await runtime.CommitAsync(async (tx, _) => { result = await change(tx).ConfigureAwait(false); }, context).ConfigureAwait(false);
            return result;
        },
        async (id, context) => await runtime.ConversationAsync(id, context).ConfigureAwait(false) is { } found ? new Handle(found.SubmitAsync, found.AbortAsync) : null,
        DurableContext.Background, keyScope, team.Options.TimeProvider ?? TimeProvider.System);

    public async Task<T> CommitAsync<T>(Func<ITx, Task<T>> change, CancellationToken cancellationToken) =>
        (T)(await commit(async tx => await change(tx).ConfigureAwait(false), Context.WithCancellation(cancellationToken)).ConfigureAwait(false))!;

    public Task<IConversationHandleLike?> ConversationAsync(ConversationId id, CancellationToken cancellationToken) =>
        conversation(id, Context.WithCancellation(cancellationToken));

    /// <summary>A message ID: deterministic within a tool call or action visit (<c>scope.n</c>), else fresh.</summary>
    public string NextId() => IdScope is null
        ? "h" + Guid.NewGuid().ToString("N")[..16]
        : IdScope + "." + Interlocked.Increment(ref sequence).ToString(CultureInfo.InvariantCulture);

    public double Now() => Clock.GetUtcNow().ToUnixTimeMilliseconds();

    private sealed class Handle(Func<SubmissionDraft, DurableContext, Task<ISubmission>> submit, Func<DurableContext, ConversationAbortOptions?, Task> abort)
        : IConversationHandleLike
    {
        public Task<ISubmission> SubmitAsync(SubmissionDraft draft, DurableContext context) => submit(draft, context);
        public Task AbortAsync(DurableContext context) => abort(context, null);
    }
}

/// <summary>What messaging uses of a conversation handle (the Harness's and the invocation-bound ones differ in type).</summary>
internal interface IConversationHandleLike
{
    Task<ISubmission> SubmitAsync(SubmissionDraft draft, DurableContext context);
    Task AbortAsync(DurableContext context);
}

/// <summary>Messaging as one address over a port.</summary>
internal sealed class Messenger(AgentTeam team, MessagingPort port, string? self) : IAgentMessenger
{
    public string Self => self ?? throw new InvalidOperationException("The calling conversation is not a team agent.");

    public async Task<AgentMessage> SendAsync(string to, string text, AgentSendOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sender = await SenderAsync(cancellationToken).ConfigureAwait(false);
        return await MessagingCore.SendAsync(team, port, sender, to, text, AgentMessageKind.Message, null, options?.Urgent ?? false, options?.Id,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentMessage> ReplyAsync(string messageId, string text, CancellationToken cancellationToken = default) =>
        await MessagingCore.ReplyAsync(team, port, await SenderAsync(cancellationToken).ConfigureAwait(false), messageId, text, cancellationToken).ConfigureAwait(false);

    public async Task<string> AskAsync(string to, string question, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        await MessagingCore.AskAsync(team, port, await SenderAsync(cancellationToken).ConfigureAwait(false), to, question, timeout ?? team.Options.AskTimeout,
            cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<AgentEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        var sender = await SenderAsync(cancellationToken).ConfigureAwait(false);
        var directory = await port.Reader.SnapshotAsync(AgentDirectory.Doc, port.Context.WithCancellation(cancellationToken)).ConfigureAwait(false);
        return [.. AgentDirectory.All(directory).Where(entry => !entry.Closed && entry.Address != sender.Address && MessagingCore.CanReach(team, sender, entry))];
    }

    public async Task<IReadOnlyList<AgentMessage>> ReadMailboxAsync(CancellationToken cancellationToken = default)
    {
        var sender = await SenderAsync(cancellationToken).ConfigureAwait(false);
        return Mailboxes.Read(await port.Reader.SnapshotAsync(Mailboxes.Doc, sender.Address, port.Context.WithCancellation(cancellationToken)).ConfigureAwait(false));
    }

    public async IAsyncEnumerable<AgentMessage> WatchMailboxAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sender = await SenderAsync(cancellationToken).ConfigureAwait(false);
        var channel = Channel.CreateUnbounded<AgentMessage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var gate = new Lock();
        void Offer(JsonObjectValue? value)
        {
            lock (gate)
                foreach (var message in Mailboxes.Read(value))
                    if (seen.Add(message.Id)) channel.Writer.TryWrite(message);
        }
        var context = port.Context.WithCancellation(cancellationToken);
        var watch = await port.Observer.WatchDocAsync(Mailboxes.Doc, sender.Address, context).ConfigureAwait(false);
        if (watch is null)
        {
            // A watch binds only an existing document: create the mailbox, then watch it.
            await port.CommitAsync(async tx =>
            {
                await Mailboxes.EnsureAsync(tx, sender.Address).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
            watch = await port.Observer.WatchDocAsync(Mailboxes.Doc, sender.Address, context).ConfigureAwait(false);
        }
        try
        {
            if (watch is not null)
            {
                Offer(watch.Value);
                watch.Start((value, _, _) => { Offer(value); return Task.CompletedTask; });
            }
            else Offer(await port.Reader.SnapshotAsync(Mailboxes.Doc, sender.Address, context).ConfigureAwait(false));
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                while (channel.Reader.TryRead(out var message)) yield return message;
        }
        finally
        {
            if (watch is not null) await watch.StopAsync().ConfigureAwait(false);
        }
    }

    public async Task AcknowledgeAsync(string messageId, CancellationToken cancellationToken = default)
    {
        var sender = await SenderAsync(cancellationToken).ConfigureAwait(false);
        await port.CommitAsync(async tx =>
        {
            Mailboxes.Acknowledge(await Mailboxes.OpenAsync(tx, sender.Address).ConfigureAwait(false), messageId);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Sender> SenderAsync(CancellationToken cancellationToken)
    {
        if (self == MessagingCore.HostAddress) return Sender.Host;
        if (self is not null) return new Sender(self, null, null, null, true);
        var directory = await port.Reader.SnapshotAsync(AgentDirectory.Doc, port.Context.WithCancellation(cancellationToken)).ConfigureAwait(false);
        var entry = AgentDirectory.FindByConversation(directory, port.Caller ?? throw new InvalidOperationException("No caller."))
            ?? throw new InvalidOperationException("The calling conversation is not a team agent.");
        return Sender.Of(entry, team.Catalog.Find(entry.Definition));
    }
}

/// <summary>Who sends: an agent (with its directory entry and definition), the host, or a workflow.</summary>
internal sealed record Sender(string Address, AgentEntry? Entry, AgentDefinition? Definition, string? Parent, bool Trusted)
{
    public static Sender Host { get; } = new(MessagingCore.HostAddress, null, null, null, true);

    public static Sender Of(AgentEntry entry, AgentDefinition? definition) => new(entry.Address, entry, definition, entry.Parent, false);
}

/// <summary>Raised when a message cannot be sent; the text is guidance for the model.</summary>
public sealed class AgentMessagingException(string message) : InvalidOperationException(message);

internal static class MessagingCore
{
    public const string HostAddress = "host";

    /// <summary>Whether <paramref name="sender"/> may message <paramref name="target"/> under its definition's scope.</summary>
    public static bool CanReach(AgentTeam team, Sender sender, AgentEntry target)
    {
        if (sender.Trusted) return true;
        var scope = sender.Definition?.Messaging ?? MessagingScope.Family;
        return scope switch
        {
            MessagingScope.None => false,
            MessagingScope.Team => true,
            _ => target.Address == sender.Parent || target.Parent == sender.Address ||
                (target.Parent is not null && target.Parent == sender.Parent) ||
                // Root agents (the orchestrator and peers) form one family.
                (target.Parent is null && sender.Parent is null),
        };
    }

    /// <summary>The refusal for an address that is not in the directory. A subagent the sender has not started yet is not an agent it can
    /// message; when the sender has delegate and spawn, the refusal says to use them.</summary>
    public static AgentMessagingException UnknownAgent(AgentTeam team, Sender sender, string address) =>
        sender.Definition is { } definition && definition.Subagents.Contains(address, StringComparer.Ordinal) &&
        (sender.Entry?.Depth ?? 0) < team.Options.MaxDepth
            ? new($"'{address}' is a subagent you have not started, so it cannot be messaged yet. Give it the work with delegate (to wait for " +
                "its answer) or spawn (to run it in the background).")
            : new($"There is no agent '{address}'; use list_agents.");

    /// <summary>Resolves <c>parent</c> and <c>self</c>.</summary>
    public static string Resolve(Sender sender, string to) => to switch
    {
        "parent" => sender.Parent ?? throw new AgentMessagingException("You have no parent agent; use list_agents to see who you can message."),
        "self" => sender.Address,
        _ => to,
    };

    /// <summary>Sends one message; idempotent by message ID.</summary>
    public static async Task<AgentMessage> SendAsync(AgentTeam team, MessagingPort port, Sender sender, string to, string text, AgentMessageKind kind,
        string? correlationId, bool urgent, string? id, CancellationToken cancellationToken, string? requestId = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var address = Resolve(sender, to);
        var messageId = id ?? port.NextId();
        var (message, target, deliver) = await port.CommitAsync(async tx =>
        {
            var directory = await tx.DocAsync(AgentDirectory.Doc).ConfigureAwait(false);
            var snapshot = (JsonObjectValue)directory.ToValue();
            AgentEntry? targetEntry = null;
            if (address == HostAddress)
            {
                if (!sender.Trusted && sender.Definition?.CanAskHost != true)
                    throw new AgentMessagingException("You may not message the host (the agent definition does not allow it).");
            }
            else if (address.StartsWith("workflow.", StringComparison.Ordinal)) { }
            else
            {
                targetEntry = AgentDirectory.Find(snapshot, address) ?? throw UnknownAgent(team, sender, address);
                if (targetEntry.Closed) throw new AgentMessagingException($"Agent '{address}' was stopped.");
                if (sender.Entry is not null && !CanReach(team, sender, targetEntry))
                    throw new AgentMessagingException($"You may not message '{address}' (outside your messaging scope); use list_agents.");
            }
            var record = new AgentMessage(messageId, sender.Address, address, kind, text, correlationId, port.Now(), false);
            // A target blocked waiting on the sender gets the message through its waiting call, not as input it could only see later. A
            // rerun keeps the first decision.
            var blockedOnSender = targetEntry is not null &&
                WaitGraph.Edges(snapshot).Any(edge => edge.From == address && edge.To == sender.Address && edge.Interruptible);
            var mailbox = await Mailboxes.OpenAsync(tx, address).ConfigureAwait(false);
            var routed = Mailboxes.Append(mailbox, record, requestId, blockedOnSender)
                ? blockedOnSender
                : Mailboxes.Routed((JsonObjectValue)mailbox.ToValue(), messageId);
            return (record, targetEntry, targetEntry is not null && !routed);
        }, cancellationToken).ConfigureAwait(false);
        if (target is not null && deliver)
        {
            var handle = await port.ConversationAsync(target.ConversationId, cancellationToken).ConfigureAwait(false)
                ?? throw new AgentMessagingException($"Agent '{address}' has no conversation.");
            await handle.SubmitAsync(SubmissionDraft.Input(Mailboxes.Header(message), urgent ? WhenBusy.Steer : WhenBusy.FollowUp,
                "agents.msg:" + message.Id, new InputEntry(Mailboxes.EntryKind, Mailboxes.EntryData(message))),
                port.Context.WithCancellation(cancellationToken)).ConfigureAwait(false);
        }
        return message;
    }

    /// <summary>Answers a question in <paramref name="sender"/>'s mailbox: a reply record for the asker's waiting call, also delivered as a
    /// message when the asker no longer waits.</summary>
    public static async Task<AgentMessage> ReplyAsync(AgentTeam team, MessagingPort port, Sender sender, string messageId, string text,
        CancellationToken cancellationToken)
    {
        var mailbox = await port.Reader.SnapshotAsync(Mailboxes.Doc, sender.Address, port.Context.WithCancellation(cancellationToken)).ConfigureAwait(false);
        var question = Mailboxes.Find(mailbox, messageId) ?? throw new AgentMessagingException($"There is no message '{messageId}' in your mailbox.");
        var request = Mailboxes.RequestOf(mailbox, messageId);
        var waiting = request is not null && (await port.Reader.SnapshotAsync(Requests.Doc, request, port.Context.WithCancellation(cancellationToken))
            .ConfigureAwait(false))?.TryGetValue("state", out var state) == true && state is JsonStringValue { Value: Requests.Waiting };
        var reply = new Sender(sender.Address, sender.Entry, sender.Definition, sender.Parent, true);
        var id = port.IdScope is null ? null : port.NextId();
        var message = await SendRecordAsync(port, reply, question.From, text, AgentMessageKind.Reply, messageId, id, cancellationToken).ConfigureAwait(false);
        if (!waiting && question.From != HostAddress && !question.From.StartsWith("workflow.", StringComparison.Ordinal))
        {
            var directory = await port.Reader.SnapshotAsync(AgentDirectory.Doc, port.Context.WithCancellation(cancellationToken)).ConfigureAwait(false);
            if (AgentDirectory.Find(directory, question.From) is { Closed: false } asker &&
                await port.ConversationAsync(asker.ConversationId, cancellationToken).ConfigureAwait(false) is { } handle)
                await handle.SubmitAsync(SubmissionDraft.Input(Mailboxes.Header(message), WhenBusy.FollowUp, "agents.msg:" + message.Id,
                    new InputEntry(Mailboxes.EntryKind, Mailboxes.EntryData(message))), port.Context.WithCancellation(cancellationToken)).ConfigureAwait(false);
        }
        await port.CommitAsync(async tx =>
        {
            Mailboxes.Acknowledge(await Mailboxes.OpenAsync(tx, sender.Address).ConfigureAwait(false), messageId);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        return message;
    }

    /// <summary>Appends a record to <paramref name="to"/>'s mailbox only.</summary>
    public static Task<AgentMessage> SendRecordAsync(MessagingPort port, Sender sender, string to, string text, AgentMessageKind kind, string? correlationId,
        string? id, CancellationToken cancellationToken, string? requestId = null)
    {
        var message = new AgentMessage(id ?? port.NextId(), sender.Address, to, kind, text, correlationId, port.Now(), false);
        return port.CommitAsync(async tx =>
        {
            Mailboxes.Append(await Mailboxes.OpenAsync(tx, to).ConfigureAwait(false), message, requestId);
            return message;
        }, cancellationToken);
    }

    /// <summary>Asks <paramref name="to"/> and waits for a reply record or the answer of the run the question starts. See
    /// <see cref="Orchestration.AskAsync"/> for the blocking rules.</summary>
    public static Task<string> AskAsync(AgentTeam team, MessagingPort port, Sender sender, string to, string question, TimeSpan timeout,
        CancellationToken cancellationToken) => Orchestration.AskAsync(team, port, sender, Resolve(sender, to), question, timeout, cancellationToken);
}
