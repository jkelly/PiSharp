// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/examples/25-compaction.ts. Upstream stands in for a model with
// pi-ai's faux provider and a response function; here TripModels does, so the example runs offline. Thresholds depend on the provider's
// reported usage, so TripModels reports the faux provider's usage estimate (packages/ai/src/providers/faux.ts withUsageEstimate, prompt
// cache simulation included). It prints what upstream's console.log prints. On a file backend the storage is then reopened in a plain
// Session and the stored summaries are read back.
//
// Two barriers keep upstream's ordering, which Node's event loop gives it and concurrent threads do not:
// - A chat answer first waits until every listed compaction has selected its range. Under Node the background compaction's selection runs
//   before the faux answer streams; an answer appended first would move the compaction's cut.
// - In part 2 the busy run's preparation waits until compact() has admitted the manual compaction, and the manual compaction's summary
//   waits until the busy run's request was sent. Under Node both orders hold: compact()'s commit lands before the preparation commit, which
//   lands before the summary does, so preparation sees a listed compaction and starts no background one. The preparation wait is a section
//   that renders nothing.
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.Contracts;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Examples;

public static partial class Examples
{
    // 25: a long trip-planning chat whose older messages are summarized so the model context stays small.
    private static async Task CompactionAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // A fake model with a tiny 3000-token window. It answers chat messages, writes summaries when asked to summarize, and once rejects a
        // request as too long, the way real providers report a context overflow.
        var models = new TripModels();

        // Generation blocks to compact above 3000 - 1000 = 2000 tokens and starts a background compaction above 2000 - 800. Settings are
        // read at every use, so the overridden getter makes BackgroundTokens live.
        var settings = new TripSettings { BackgroundTokens = 800 };
        // Preparation passes this section's render, which shows nothing, once the gate it awaits opens (see the file header).
        Task preparation = Task.CompletedTask;
        var registry = Registry.Create();
        registry.Install(Define.Extension(new Extension
        {
            Name = "example.pace",
            Sections = [Define.Section("pace", async (_, _) =>
            {
                await Volatile.Read(ref preparation);
                return null;
            })],
        }));
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions { Models = models, Registry = registry, Settings = settings }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = new ModelRef("faux", "tiny") });
        models.BeforeAnswer = () => RangesSelectedAsync(harness, root.Id);

        async Task Ask(string question)
        {
            var submission = await root.SubmitAsync(SubmissionDraft.Input(question), Context);
            var record = await submission.WaitAsync(Context);
            // Let a background compaction started by this turn finish, so its summary shows below.
            var live = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context);
            var running = live is not null && live.TryGetValue("compactions", out var listed) ? (JsonArrayValue)listed : JsonArrayValue.Empty;
            foreach (var status in running) await harness.WaitForTaskAsync(TaskIdOf(((JsonObjectValue)status)["taskId"]), Context);
            await ShowAsync(root, $"after \"{question}\" ({(record.Status == SubmissionStatus.Done ? "answered" : record.Reason)})", output);
        }

        // 1. A long chat: once the context crosses the background threshold, a compaction runs while the chat goes on, and its summary lands
        // at once when the conversation is idle, otherwise at the next turn boundary.
        foreach (var question in new[] { "Where should we stay?", "What should we eat?", "Which day trips?", "Any museums?", "Nightlife?" })
            await Ask(question);

        // 2. A manual compaction while an answer is still being written. The summary is ready first, waits in the inbox, and is placed right
        // after the answer.
        var finishAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        models.Hold = finishAnswer.Task;
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref preparation, admitted.Task);
        var busy = await root.SubmitAsync(SubmissionDraft.Input("How do we get around?"), Context);
        var manual = await root.CompactAsync("Keep the hotel shortlist", Context);
        admitted.SetResult();
        Volatile.Write(ref preparation, Task.CompletedTask);
        var state = (await harness.WaitForTaskAsync(manual, Context)).State;
        var placement = ((SettlingTaskState)state).Outcome is CompletedTaskOutcome { Result: { } result } ? CompactionResult.FromJson(result).SubmissionId : null;
        var summary = (await harness.SubmissionAsync(placement!.Value, Context))!;
        await output.WriteLineAsync($"\nmanual compaction finished; its summary is {StatusName((await summary.StatusAsync(Context)).Status)}");
        finishAnswer.SetResult();
        await busy.WaitAsync(Context);
        await output.WriteLineAsync($"after the answer, the summary is {StatusName((await summary.WaitAsync(Context)).Status)}");
        await ShowAsync(root, "after compact()", output);

        // 3. The provider rejects a request as too long: generation compacts and retries it once. Background compaction is turned off so the
        // summary below is the overflow one.
        settings.BackgroundTokens = 0;
        await Ask("What should we pack?");
        models.OverflowOnce = true;
        await Ask("Summarize the plan for my partner");

        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
        {
            var entries = await TranscriptAsync(reopened, root.Id, 1000);
            var reasons = entries.Where(entry => Entries.CompactionEntry.Is(entry))
                .Select(entry => (JsonValue)((JsonStringValue)((JsonObjectValue)entry.Data!)["reason"]).Value);
            await output.WriteLineAsync($"{entries.Count} entries stored, summaries: {Json(JsonValue.Array(reasons))}");
        });
    }

    /// <summary>Prints the model context: one line per message, and the number of stored entries behind it.</summary>
    private static async Task ShowAsync(IConversation conversation, string label, TextWriter output)
    {
        var view = await conversation.ContextAsync(Context);
        var stored = (await conversation.EntriesAsync(new EntryQueryBounds(), 1000, null, Context)).Items.Length;
        await output.WriteLineAsync($"\n{label}: {view.Messages.Length} messages in context, {stored} entries stored");
        if (view.Head is { } head && Entries.CompactionEntry.Is(head))
            await output.WriteLineAsync($"  ({((JsonStringValue)((JsonObjectValue)head.Data!)["reason"]).Value} compaction summary first)");
        foreach (var message in view.Messages)
        {
            var role = RoleOf(message);
            var text = role == "system" ? "(system prompt)" : MessageText(message);
            await output.WriteLineAsync($"  {role,-9} {(role == "system" ? text : text[..Math.Min(70, text.Length)])}");
        }
    }

    /// <summary>Resolves once no listed compaction of <paramref name="conversation"/> is still selecting its range (see the file header).</summary>
    private static async Task RangesSelectedAsync(IHarness harness, ConversationId conversation)
    {
        for (var attempt = 0; attempt < 4000; attempt++)
        {
            var live = await harness.SnapshotAsync(Live.LiveDoc, conversation, Context);
            var selecting = false;
            if (live is not null && live.TryGetValue("compactions", out var listed))
                foreach (var status in (JsonArrayValue)listed)
                {
                    var task = await harness.GetTaskAsync(TaskIdOf(((JsonObjectValue)status)["taskId"]), Context);
                    if (task?.State is LiveTaskState { Checkpoint: JsonObjectValue checkpoint } &&
                        checkpoint["phase"] is JsonStringValue { Value: "select" })
                        selecting = true;
                }
            if (!selecting) return;
            await Task.Delay(5);
        }
        throw new TimeoutException("A compaction did not select its range");
    }

    private static TaskId TaskIdOf(JsonValue id) => new((long)((JsonNumberValue)id).Value);

    private static string StatusName(SubmissionStatus status) => status switch
    {
        SubmissionStatus.Queued => "queued",
        SubmissionStatus.Placed => "placed",
        SubmissionStatus.Done => "done",
        _ => "unanswered",
    };

    private static string RoleOf(JsonValue message) => ((JsonStringValue)((JsonObjectValue)message)["role"]).Value;

    /// <summary>Upstream <c>text(message)</c>: string content, or the first text block with newlines as spaces.</summary>
    private static string MessageText(JsonValue? message)
    {
        if (message is not JsonObjectValue value || RoleOf(value) == "system") return "";
        if (value["content"] is JsonStringValue content) return content.Value;
        return value["content"] is JsonArrayValue blocks &&
            blocks.OfType<JsonObjectValue>().FirstOrDefault(block => block["type"] is JsonStringValue { Value: "text" }) is { } text
            ? ((JsonStringValue)text["text"]).Value.Replace("\n", " ", StringComparison.Ordinal) : "";
    }

    /// <summary>Harness settings whose compaction policy reads <see cref="BackgroundTokens"/> at every use.</summary>
    private sealed class TripSettings : HarnessSettings
    {
        private double backgroundTokens;

        public double BackgroundTokens
        {
            get => Volatile.Read(ref backgroundTokens);
            set => Volatile.Write(ref backgroundTokens, value);
        }

        public override CompactionSettings? Compaction
        {
            get => new() { ReserveTokens = 1000, KeepRecentTokens = 400, BackgroundTokens = BackgroundTokens };
            init { }
        }
    }

    /// <summary>
    /// The example's model faux/tiny (3000-token window, 1000 output tokens): summaries for summarization requests, one "prompt is too long"
    /// rejection while <see cref="OverflowOnce"/> is set, and otherwise a long answer to the last user message, after <see cref="Hold"/> when
    /// set. Usage is the faux provider's estimate.
    /// </summary>
    private sealed class TripModels : IDurableModels
    {
        private readonly Lock gate = new();
        private readonly Dictionary<string, string[]> promptCache = new(StringComparer.Ordinal);
        private int summaries;
        private volatile bool overflowOnce;
        private Task? hold;
        private TaskCompletionSource? holdTaken;

        public DurableModel Model { get; } = new()
        {
            Id = "tiny", Name = "tiny", Api = "faux", Provider = "faux", BaseUrl = "http://localhost:0", Input = ["text", "image"],
            ContextWindow = 3000, MaxTokens = 1000,
        };

        public bool OverflowOnce { get => overflowOnce; set => overflowOnce = value; }

        /// <summary>While set, the next chat answer waits for it, which keeps the conversation busy; until that request arrives, summaries
        /// wait for it (see the file header).</summary>
        public Task? Hold
        {
            get { lock (gate) return hold; }
            set
            {
                lock (gate)
                {
                    hold = value;
                    holdTaken = value is null ? null : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }

        /// <summary>A barrier every chat answer passes first (see the file header).</summary>
        public Func<Task>? BeforeAnswer { get; set; }

        public DurableModel? GetModel(string provider, string modelId) => provider == Model.Provider && modelId == Model.Id ? Model : null;

        private async Task<AssistantMessage> RespondAsync(ImmutableArray<JsonValue> messages, DurableStreamOptions? options, CancellationToken cancellationToken)
        {
            var first = messages.Length > 0 ? (JsonObjectValue)messages[0] : null;
            if (first is not null && RoleOf(first) == "system" && first["content"] is JsonStringValue { Value: var system } &&
                system.Contains("summarization", StringComparison.Ordinal))
            {
                Task? held;
                lock (gate) held = holdTaken?.Task;
                if (held is not null) await held.WaitAsync(cancellationToken);
                var number = Interlocked.Increment(ref summaries);
                return Estimate(Message($"## Goal\nPlan a week in Lisbon (summary #{number})."), messages, options);
            }
            if (overflowOnce)
            {
                overflowOnce = false;
                return Estimate(Message("", StopReason.Error, "prompt is too long"), messages, options);
            }
            Task? release;
            lock (gate)
            {
                release = hold;
                hold = null;
                holdTaken?.TrySetResult();
                holdTaken = null;
            }
            if (release is not null) await release.WaitAsync(cancellationToken);
            if (BeforeAnswer is { } barrier) await barrier();
            var question = messages.LastOrDefault(message => RoleOf(message) == "user");
            return Estimate(Message($"A detailed answer to \"{MessageText(question)}\": {string.Concat(Enumerable.Repeat("details ", 200))}"),
                messages, options);
        }

        private AssistantMessage Message(string text, StopReason stopReason = StopReason.Stop, string? errorMessage = null) => new(
            Model.Api, Model.Provider, Model.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [new TextContent(text)], TokenUsage.Zero, stopReason,
            errorMessage is null ? null : JsonFields.Empty.Set("errorMessage", JsonData.Parse(JsonValue.String(errorMessage).ToJson())));

        public async IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            var message = await RespondAsync(messages, options, cancellationToken);
            yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending, Usage = TokenUsage.Zero });
            var text = ((TextContent)message.Content[0]).Text;
            yield return new TextStarted(0, new TextContent(""));
            if (text.Length > 0) yield return new TextDelta(0, text);
            yield return new TextEnded(0, text);
            yield return message.StopReason == StopReason.Error ? new StreamError(StopReason.Error, message) : new StreamDone(message.StopReason, message);
        }

        public async Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
            CancellationToken cancellationToken)
        {
            StreamTerminalEvent? terminal = null;
            await foreach (var item in StreamSimple(model, messages, options, cancellationToken)) if (item is StreamTerminalEvent end) terminal = end;
            return terminal!.Message;
        }

        public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The example model does not defer responses.");

        public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            Task.FromException<AssistantMessage>(new NotSupportedException("The example model does not defer responses."));

        public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) => Task.CompletedTask;

        // ─── The faux provider's usage estimate (withUsageEstimate) ───

        private AssistantMessage Estimate(AssistantMessage message, ImmutableArray<JsonValue> messages, DurableStreamOptions? options)
        {
            // One text per message; the whole prompt joins them with blank lines.
            var prompt = messages.Select(item => $"{RoleOf(item)}:{PromptText((JsonObjectValue)item)}").ToArray();
            var promptLength = JoinedLength(prompt, prompt.Length);
            var promptTokens = Math.Ceiling(promptLength / 4.0);
            var output = Math.Ceiling(string.Join("\n", message.Content.OfType<TextContent>().Select(text => text.Text)).Length / 4.0);
            double input = promptTokens, cacheRead = 0, cacheWrite = 0;
            if (options?.SessionId is { Length: > 0 } sessionId && options.CacheRetention != "none")
            {
                lock (gate)
                {
                    if (promptCache.TryGetValue(sessionId, out var previous))
                    {
                        var cached = CommonPromptPrefixLength(previous, prompt);
                        cacheRead = Math.Ceiling(cached / 4.0);
                        cacheWrite = Math.Ceiling((promptLength - cached) / 4.0);
                        input = Math.Max(0, promptTokens - cacheRead);
                    }
                    else cacheWrite = promptTokens;
                    promptCache[sessionId] = prompt;
                }
            }
            return message with { Usage = new TokenUsage(input, output, cacheRead, cacheWrite, input + output + cacheRead + cacheWrite, new(0, 0, 0, 0, 0)) };
        }

        private static string PromptText(JsonObjectValue message)
        {
            switch (RoleOf(message))
            {
                case "system":
                {
                    var parts = new List<string> { ContentText(message["content"]) };
                    if (message.TryGetValue("sections", out var sections))
                        parts.AddRange(((JsonObjectValue)sections).Values.OfType<JsonStringValue>().Select(text => text.Value));
                    var text = string.Join("\n\n", parts.Where(part => part.Length > 0));
                    var lines = new List<string> { text };
                    if (message.TryGetValue("toolsRemoved", out var removed)) lines.AddRange(((JsonArrayValue)removed).Select(tool => $"tool-:{tool.ToJson()}"));
                    if (message.TryGetValue("toolsAdded", out var added)) lines.AddRange(((JsonArrayValue)added).Select(tool => $"tool+:{tool.ToJson()}"));
                    return string.Join("\n", lines.Where(part => part.Length > 0));
                }
                case "user": return ContentText(message["content"]);
                default:
                    return string.Join("\n", ((JsonArrayValue)message["content"]).OfType<JsonObjectValue>().Select(block =>
                        ((JsonStringValue)block["type"]).Value switch
                        {
                            "text" => ((JsonStringValue)block["text"]).Value,
                            "thinking" => ((JsonStringValue)block["thinking"]).Value,
                            _ => $"{((JsonStringValue)block["name"]).Value}:{block["arguments"].ToJson()}",
                        }));
            }
        }

        private static string ContentText(JsonValue content) => content is JsonStringValue text ? text.Value
            : string.Join("\n", ((JsonArrayValue)content).OfType<JsonObjectValue>().Where(block => block["type"] is JsonStringValue { Value: "text" })
                .Select(block => ((JsonStringValue)block["text"]).Value));

        private static double JoinedLength(string[] messages, int count)
        {
            double length = count > 0 ? (count - 1) * 2 : 0;
            for (var index = 0; index < count; index++) length += messages[index].Length;
            return length;
        }

        private static double CommonPromptPrefixLength(string[] previous, string[] current)
        {
            var index = 0;
            while (index < previous.Length && index < current.Length && previous[index] == current[index]) index++;
            string Rest(string[] messages) => index == messages.Length ? "" : (index > 0 ? "\n\n" : "") + string.Join("\n\n", messages.Skip(index));
            var a = Rest(previous);
            var b = Rest(current);
            var common = 0;
            while (common < Math.Min(a.Length, b.Length) && a[common] == b[common]) common++;
            return JoinedLength(previous, index) + common;
        }
    }
}
