// Decision 0011 (run W2, WU8): what the chat partials know about each transcript item beyond PiSharp.Durable.Client's Transcript, read from
// the stored entries: a message's time, usage, cost, duration and stop reason (the footer), how it failed (error, expired or aborted), whether
// a restart cut a tool call off (the tool result's restart diagnostic, "interrupted" or "abandoned"), whether an aborted partial was answered
// again with no message of yours in between (a restart recovery), whether an error was retried automatically, and which item is the running
// turn's tail (the live footer and the "kept running while you were away" note). Facts are plain records, so the stream's item fingerprint
// (AgentViewHub.Print) changes when they do. Nothing here is guessed: a fact the entries do not hold stays null and the partial leaves it out.
using System.Text.Json.Nodes;
using PiSharp.Durable.Client;

namespace PiSharp.WebAgent.Chat;

/// <summary>A stored message's metadata (the first message of an entry).</summary>
public sealed record ChatEntryMeta
{
    /// <summary>When the message was made, in Unix milliseconds.</summary>
    public double? Time { get; init; }

    /// <summary>The provider that answered.</summary>
    public string? Provider { get; init; }

    /// <summary>The model that answered.</summary>
    public string? Model { get; init; }

    /// <summary>The provider API.</summary>
    public string? Api { get; init; }

    /// <summary><c>stop</c>, <c>toolUse</c>, <c>length</c>, <c>error</c> or <c>aborted</c>.</summary>
    public string? StopReason { get; init; }

    /// <summary>The failure's message, if any.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Input tokens (not cached).</summary>
    public double Input { get; init; }

    /// <summary>Output tokens.</summary>
    public double Output { get; init; }

    /// <summary>Cached input tokens read.</summary>
    public double CacheRead { get; init; }

    /// <summary>Input tokens written to the cache.</summary>
    public double CacheWrite { get; init; }

    /// <summary>The cost the engine reported, in US dollars.</summary>
    public double Cost { get; init; }

    /// <summary>How long the message took, when the provider layer recorded it.</summary>
    public double? DurationMs { get; init; }

    /// <summary>The op of the Spend Guard ledger row the answer settled (W3 fix: the guard stores it on the message as <c>pisharp.spendOp</c>),
    /// or null for an answer the guard did not reserve (or one stored before it named its row).</summary>
    public string? SpendOp { get; init; }

    /// <summary>Whether the provider reported any usage.</summary>
    public bool HasUsage => Input + Output + CacheRead + CacheWrite > 0;

    /// <summary>The metadata of an entry's first assistant message (or its first message), or null for an entry without messages.</summary>
    public static ChatEntryMeta? Of(JsonObject? entry)
    {
        if (entry?["model"] is not JsonArray messages) return null;
        var list = messages.OfType<JsonObject>().ToList();
        var message = list.FirstOrDefault(item => ChatDisplay.Str(item, "role") == "assistant") ?? list.FirstOrDefault();
        if (message is null) return null;
        var usage = message["usage"] as JsonObject;
        return new ChatEntryMeta
        {
            Time = Number(message, "timestamp"),
            Provider = ChatDisplay.Str(message, "provider") is { Length: > 0 } provider ? provider : null,
            Model = ChatDisplay.Str(message, "model") is { Length: > 0 } model ? model : null,
            Api = ChatDisplay.Str(message, "api") is { Length: > 0 } api ? api : null,
            StopReason = ChatDisplay.Str(message, "stopReason"),
            ErrorMessage = ChatDisplay.Str(message, "errorMessage") is { Length: > 0 } error ? error : null,
            Input = Number(usage, "input") ?? 0,
            Output = Number(usage, "output") ?? 0,
            CacheRead = Number(usage, "cacheRead") ?? 0,
            CacheWrite = Number(usage, "cacheWrite") ?? 0,
            Cost = Number(usage?["cost"] as JsonObject, "total") ?? 0,
            DurationMs = Number(message, "durationMs"),
            SpendOp = Spend.GuardedDurableModels.LedgerOpOf(message),
        };
    }

    internal static double? Number(JsonObject? json, string key) =>
        json?[key] is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : null;
}

/// <summary>A tool call's stored result: the restart or abort diagnostic it ended with, its duration and when it was written.</summary>
/// <param name="EntryId">The <c>pi.tool-result</c> entry.</param>
/// <param name="Code">The deciding diagnostic code: <c>interrupted</c> or <c>abandoned</c> (a restart), <c>aborted</c> (a Stop), another
/// error code, or null.</param>
/// <param name="DurationMs">How long the call ran, when recorded.</param>
/// <param name="Time">When the result was written, in Unix milliseconds.</param>
public sealed record ChatToolResultMeta(long EntryId, string? Code, double? DurationMs, double? Time)
{
    /// <summary>Cut off by a restart: it may have partly run (<c>interrupted</c>) or never started (<c>abandoned</c>), and it is not run
    /// again by itself.</summary>
    public bool Restarted => Code is "interrupted" or "abandoned";
}

/// <summary>What the agent is doing, for the running turn's tail.</summary>
/// <param name="Running">A run is going.</param>
/// <param name="Generating">An answer streams.</param>
/// <param name="WaitingApproval">A tool call of the agent waits for an approval.</param>
/// <param name="WaitingForYou">A question form or plan review of the agent waits.</param>
public sealed record ChatRunState(bool Running, bool Generating, bool WaitingApproval, bool WaitingForYou)
{
    /// <summary>Nothing runs.</summary>
    public static ChatRunState Idle { get; } = new(false, false, false, false);
}

/// <summary>The facts of one transcript item.</summary>
public sealed record ChatItemFacts
{
    /// <summary>No facts.</summary>
    public static ChatItemFacts Empty { get; } = new();

    /// <summary>The entry's metadata.</summary>
    public ChatEntryMeta? Meta { get; init; }

    /// <summary>How the answer ended.</summary>
    public ChatFailureKind Failure { get; init; }

    /// <summary>The failed attempts in a row that end with this one (the engine's automatic retries included).</summary>
    public int Attempts { get; init; }

    /// <summary>An aborted partial answered again by the next assistant message, with no message of yours in between (a restart cut the
    /// stream, and the run picked up from its last saved point).</summary>
    public bool AnsweredAgain { get; init; }

    /// <summary>An answer that follows such an aborted partial.</summary>
    public bool AnsweredAfterRestart { get; init; }

    /// <summary>A failed request the engine retried by itself (another answer follows with no message of yours in between).</summary>
    public bool RetriedAutomatically { get; init; }

    /// <summary>A tool call of this answer was cut off by a restart.</summary>
    public bool Interrupted { get; init; }

    /// <summary>When the restart's tool result was written (the app was back), in Unix milliseconds.</summary>
    public double? RestartedAt { get; init; }

    /// <summary>The assistant turn within the agent run (1 for the first answer after your message).</summary>
    public int Turn { get; init; }

    /// <summary>The running turn's last item.</summary>
    public bool Tail { get; init; }

    /// <summary>For the tail: <c>streaming</c>, <c>working</c>, <c>waiting for approval</c> or <c>waiting for you</c>.</summary>
    public string? LiveStatus { get; init; }

    /// <summary>For the tail: when the run's message was sent, in Unix milliseconds.</summary>
    public double? RunStarted { get; init; }
}

/// <summary>Builds the facts of a transcript.</summary>
public static class ChatFacts
{
    /// <summary>The facts of <paramref name="items"/> by item key, from the committed <paramref name="entries"/> (ordered by id).</summary>
    public static IReadOnlyDictionary<string, ChatItemFacts> Build(IReadOnlyList<TranscriptItem> items, IReadOnlyList<JsonObject> entries, ChatRunState? run = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(entries);
        run ??= ChatRunState.Idle;
        var ordered = entries.OrderBy(entry => ChatDisplay.Long(entry, "id") ?? 0).ToList();
        var results = ToolResults(ordered);
        var facts = new Dictionary<string, ChatItemFacts>(StringComparer.Ordinal);
        var turn = 0;
        var attempts = 0;
        var previousAborted = false;
        double? runStarted = null;
        for (var index = 0; index < ordered.Count; index++)
        {
            var entry = ordered[index];
            var kind = ChatDisplay.Str(entry, "kind");
            var key = "e" + (ChatDisplay.Long(entry, "id") ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (kind == "pi.user")
            {
                var meta = ChatEntryMeta.Of(entry);
                facts[key] = new ChatItemFacts { Meta = meta };
                turn = 0;
                attempts = 0;
                previousAborted = false;
                runStarted = meta?.Time;
                continue;
            }
            if (kind != "pi.assistant") continue;
            turn++;
            var info = ChatEntryMeta.Of(entry);
            var failure = ChatFailures.Classify(info?.StopReason, info?.ErrorMessage);
            attempts = failure is ChatFailureKind.Error or ChatFailureKind.Expired ? attempts + 1 : 0;
            var answered = NextMessageKind(ordered, index) == "pi.assistant";
            var restarted = CallIds(entry).Select(id => results.GetValueOrDefault(id)).Where(result => result is { Restarted: true }).ToList();
            facts[key] = new ChatItemFacts
            {
                Meta = info,
                Failure = failure,
                Attempts = attempts,
                AnsweredAgain = failure == ChatFailureKind.Aborted && answered,
                AnsweredAfterRestart = previousAborted,
                RetriedAutomatically = failure == ChatFailureKind.Error && answered,
                Interrupted = restarted.Count > 0,
                RestartedAt = restarted.Count > 0 ? restarted.Max(result => result!.Time) : null,
                Turn = turn,
            };
            previousAborted = failure == ChatFailureKind.Aborted;
        }
        if (run.Running && items.LastOrDefault(item => item is AssistantItem) is AssistantItem tail)
        {
            var known = facts.GetValueOrDefault(tail.Key) ?? ChatItemFacts.Empty;
            facts[tail.Key] = known with
            {
                Tail = true,
                Turn = tail.Id is null ? turn + 1 : known.Turn,
                RunStarted = runStarted,
                LiveStatus = run.WaitingApproval ? "waiting for approval" : run.WaitingForYou ? "waiting for you" : run.Generating ? "streaming" : "working",
            };
        }
        return facts;
    }

    /// <summary>The stored result of each tool call, by call id.</summary>
    public static IReadOnlyDictionary<string, ChatToolResultMeta> ToolResults(IReadOnlyList<JsonObject> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var results = new Dictionary<string, ChatToolResultMeta>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (ChatDisplay.Str(entry, "kind") != "pi.tool-result") continue;
            var codes = ChatDisplay.Objects(entry["data"] as JsonObject, "diagnostics").Select(diagnostic => ChatDisplay.Str(diagnostic, "code"))
                .OfType<string>().ToList();
            var code = codes.FirstOrDefault(item => item is "interrupted" or "abandoned") ?? codes.FirstOrDefault(item => item == "aborted") ?? codes.LastOrDefault();
            foreach (var message in entry["model"] is JsonArray model ? model.OfType<JsonObject>() : [])
                if (ChatDisplay.Str(message, "role") == "toolResult" && ChatDisplay.Str(message, "toolCallId") is { } callId)
                    results[callId] = new ChatToolResultMeta(ChatDisplay.Long(entry, "id") ?? 0, code, ChatEntryMeta.Number(message, "durationMs"),
                        ChatEntryMeta.Number(message, "timestamp"));
        }
        return results;
    }

    /// <summary>The newest assistant entry's failure, with its metadata (for the expired banner).</summary>
    public static (ChatFailureKind Kind, ChatEntryMeta? Meta) LastFailure(IReadOnlyList<JsonObject> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var last = entries.Where(entry => ChatDisplay.Str(entry, "kind") == "pi.assistant").MaxBy(entry => ChatDisplay.Long(entry, "id") ?? 0);
        var meta = ChatEntryMeta.Of(last);
        return (ChatFailures.Classify(meta?.StopReason, meta?.ErrorMessage), meta);
    }

    /// <summary>The kind of the first user or assistant entry after <paramref name="index"/>, or null.</summary>
    private static string? NextMessageKind(IReadOnlyList<JsonObject> ordered, int index)
    {
        for (var next = index + 1; next < ordered.Count; next++)
            if (ChatDisplay.Str(ordered[next], "kind") is ("pi.user" or "pi.assistant") and var kind) return kind;
        return null;
    }

    private static IEnumerable<string> CallIds(JsonObject entry) =>
        (entry["model"] as JsonArray ?? []).OfType<JsonObject>().Where(message => ChatDisplay.Str(message, "role") == "assistant")
            .SelectMany(message => (message["content"] as JsonArray ?? []).OfType<JsonObject>())
            .Where(block => ChatDisplay.Str(block, "type") == "toolCall")
            .Select(block => ChatDisplay.Str(block, "id")).OfType<string>();
}
