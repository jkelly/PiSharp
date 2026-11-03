using System.Collections.Concurrent;
using PiSharp.AI;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;

// Decision 0011 (WU1): DurableDeviceOptions.SessionStream. The host's fields and a per-request `op` are saved with each prepared request,
// reused when the request is recovered after a restart, new for every new request, and never sent to a provider.
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> StreamMetadataCases() =>
    [
        Case("stream-metadata.fields-and-op-are-saved-with-the-prepared-request", async () =>
        {
            var held = new HeldModel();
            await using var device = await TestDevice.OpenAsync("metadata-saved", held.Setup(_ => Account("acct-1")));
            var session = await device.CreateAsync("saved");
            await session.SendAsync(1, new SendRequest("hold: one", "saved-1"));
            var request = await held.NextAsync();
            var op = SessionStreamMetadata.Operation(request);
            Check(Guid.TryParse(op, out _), "op is a UUID: " + op);
            Equal("acct-1", SessionStreamMetadata.Field(request, "account"), "the account reached the model");
            Equal("default", SessionStreamMetadata.Field(request, "tier"), "the tier reached the model");

            var pinned = await PinnedStreamOptionsAsync(session);
            var saved = SessionStreamMetadata.Read(pinned.TryGetValue("metadata", out var metadata) ? metadata as JsonObjectValue : null)!;
            Equal("""{"account":"acct-1","tier":"default","op":"__"}""".Replace("__", op, StringComparison.Ordinal), saved.ToJson(),
                "the request checkpoint pins the same object");
            held.Release.TrySetResult();
            var done = await session.SendAsync(1, new SendRequest("hold: one", "saved-1") { Wait = Answer });
            Equal("released", done.AnswerText, "answered");
        }),

        Case("stream-metadata.a-recovered-request-keeps-its-op-and-account-after-a-restart", async () =>
        {
            var account = "acct-1";
            var held = new HeldModel();
            var setup = held.Setup(_ => Account(Volatile.Read(ref account)));
            await using var device = await TestDevice.OpenAsync("metadata-recovery", setup);
            var session = await device.CreateAsync("recovery");
            await session.SendAsync(1, new SendRequest("hold: across a restart", "recover-1"));
            var first = await held.NextAsync();

            // The chat moves to another account while the request is in flight, then the device restarts before it answers.
            Volatile.Write(ref account, "acct-2");
            await device.ReopenAsync(setup);
            var recovered = await held.NextAsync();
            Equal(SessionStreamMetadata.Operation(first), SessionStreamMetadata.Operation(recovered), "the recovered request keeps its op");
            Equal("acct-1", SessionStreamMetadata.Field(recovered, "account"), "and the account it was prepared with");
            held.Release.TrySetResult();
            var reopened = device.Host.ResolveSession(session.Id);
            var done = await reopened.SendAsync(1, new SendRequest("hold: across a restart", "recover-1") { Wait = Answer });
            Equal(SendStatus.Done, done.Status, "the run finished after the restart");
            Equal("released", done.AnswerText, "one answer");

            var next = await reopened.SendAsync(1, new SendRequest("after the restart", "recover-2") { Wait = Answer });
            Equal("Echo: after the restart", next.AnswerText, "a new request");
            var fresh = await held.NextAsync();
            Check(SessionStreamMetadata.Operation(fresh) is { } op && op != SessionStreamMetadata.Operation(first), "a new request gets a new op");
            Equal("acct-2", SessionStreamMetadata.Field(fresh, "account"), "a new request follows the new binding");
        }),

        Case("stream-metadata.every-request-of-a-run-and-every-summary-gets-a-new-op", async () =>
        {
            var gate = new TestGate();
            var held = new HeldModel();
            var setup = held.Setup(_ => Account("acct-1")) with { Registry = RegistryWith(gate) };
            await using var device = await TestDevice.OpenAsync("metadata-fresh", setup);
            var session = await device.CreateAsync("fresh");
            var tool = await session.SendAsync(1, new SendRequest("tool: probe {}", "fresh-1") { Wait = Answer });
            Equal("Tool finished: probe: ok", tool.AnswerText, "a run of two requests");
            var words = string.Join(' ', Enumerable.Repeat("durable", 900));
            for (var index = 0; index < 3; index++)
                await session.SendAsync(1, new SendRequest($"{index} {words}", $"fresh-long-{index}") { Wait = Answer });
            session.UpdateCompaction(new SessionCompactionChange { KeepRecentTokens = 500 });
            var compaction = await session.CompactAsync(1);
            await session.GetHarness().WaitForTaskAsync(compaction, DurableContext.Background).WaitAsync(Answer);

            var requests = held.All;
            Equal(6, requests.Count, "two requests for the tool run, three answers and one summary");
            Check(requests[^1]?.MaxTokens is not null, "the last request is the summary");
            var ops = requests.Select(SessionStreamMetadata.Operation).ToList();
            Check(ops.All(op => op is not null), "every request carries an op");
            Equal(ops.Count, ops.Distinct().Count(), "every op is new: " + string.Join(", ", ops));
            Check(requests.All(request => SessionStreamMetadata.Field(request, "account") == "acct-1"), "every request carries the account");
        }),

        Case("stream-metadata.without-a-hook-requests-carry-no-pisharp-object", async () =>
        {
            var held = new HeldModel();
            await using var device = await TestDevice.OpenAsync("metadata-none", held.Setup(null));
            var session = await device.CreateAsync("none");
            await session.SendAsync(1, new SendRequest("plain", "none-1") { Wait = Answer });
            var request = await held.NextAsync();
            Equal(null, SessionStreamMetadata.Read(request), "no pisharp object");
        }),

        Case("stream-metadata.a-throwing-hook-is-reported-and-the-request-carries-op-alone", async () =>
        {
            var reports = new ConcurrentQueue<Exception>();
            var held = new HeldModel();
            var throwing = held.Setup(_ => throw new InvalidOperationException("binding store offline"));
            var setup = throwing with
            {
                Configure = options => throwing.Configure!(options) with { OnReport = (_, failure) => reports.Enqueue(failure) },
            };
            await using var device = await TestDevice.OpenAsync("metadata-throws", setup);
            var session = await device.CreateAsync("throws");
            var done = await session.SendAsync(1, new SendRequest("still answered", "throws-1") { Wait = Answer });
            Equal("Echo: still answered", done.AnswerText, "the run is not broken");
            var pisharp = SessionStreamMetadata.Read(await held.NextAsync())!;
            Equal("op", string.Join(",", pisharp.Keys), "op alone");
            Check(reports.Any(failure => failure.Message == "binding store offline"), "the failure was reported");
        }),

        Case("stream-metadata.helpers-reserve-op-and-strip-only-pisharp", () =>
        {
            var fields = SessionStreamMetadata.Fields(("account", "acct-9"), ("tier", null), ("op", "forged"));
            Equal("""{"account":"acct-9","op":"forged"}""", fields.ToJson(), "null values are left out");
            var attached = SessionStreamMetadata.Attach(new ConversationStreamOptions
            {
                Metadata = JsonValue.Object([new("user_id", "user-7")]),
            }, SessionStreamMetadata.Create(fields, "op-1"));
            Equal("""{"user_id":"user-7","pisharp":{"account":"acct-9","op":"op-1"}}""", attached.Metadata!.ToJson(), "the host's op wins");
            var options = new DurableStreamOptions { Metadata = attached.Metadata, MaxTokens = 10 };
            Equal("op-1", SessionStreamMetadata.Operation(options), "op");
            Equal("acct-9", SessionStreamMetadata.Field(options, "account"), "account");
            Equal(null, SessionStreamMetadata.Field(options, "tier"), "absent");
            var stripped = SessionStreamMetadata.Strip(options)!;
            Equal("""{"user_id":"user-7"}""", stripped.Metadata!.ToJson(), "other metadata stays");
            Equal(10d, stripped.MaxTokens, "other options stay");
            var only = SessionStreamMetadata.Strip(new DurableStreamOptions { Metadata = JsonValue.Object([new("pisharp", fields)]) })!;
            Equal(null, only.Metadata, "empty metadata becomes null");
            var plain = new DurableStreamOptions { Reasoning = "low" };
            Check(ReferenceEquals(plain, SessionStreamMetadata.Strip(plain)), "options without it come back as they are");
            Equal(null, SessionStreamMetadata.Strip(null), "null stays null");
        }),

        Case("stream-metadata.native-routes-never-send-it-to-the-provider", async () =>
        {
            var handler = new ProviderRecorder();
            var routed = new ConcurrentQueue<DurableStreamOptions?>();
            using var models = new NativeDurableModels(new NativeDurableModelsOptions
            {
                Providers = ["openai", "anthropic"],
                HttpHandler = handler,
                ResolveApiKey = (_, _) => ValueTask.FromResult<string?>("test-provider-key"),
                HostTransport = (_, options, _) =>
                {
                    routed.Enqueue(options);
                    return ValueTask.FromResult<IChatTransport?>(null);
                },
            });
            var setup = new DeviceSetup
            {
                DefaultModel = new ModelRef("openai", "gpt-4.1"),
                Configure = options => options with
                {
                    Models = models,
                    SessionStream = _ => Account("acct-secret-route"),
                    Settings = new HarnessSettings { Stream = new ConversationStreamOptions { Metadata = JsonValue.Object([new("user_id", "user-7")]) } },
                },
            };
            await using var device = await TestDevice.OpenAsync("metadata-native", setup);
            var openai = await device.CreateAsync("openai");
            Equal("Hello world", (await openai.SendAsync(1, new SendRequest("hi", "native-1") { Wait = Answer })).AnswerText, "the openai route answered");
            var anthropic = await device.CreateAsync("anthropic", new ModelRef("anthropic", "claude-haiku-4-5"));
            Equal("Hello there", (await anthropic.SendAsync(1, new SendRequest("hi", "native-2") { Wait = Answer })).AnswerText,
                "the anthropic route answered");

            Equal(2, routed.Count, "the host route saw both requests");
            var ops = routed.Select(SessionStreamMetadata.Operation).ToList();
            Check(routed.All(options => SessionStreamMetadata.Field(options, "account") == "acct-secret-route"), "the host route reads the account");
            var sent = handler.Requests.ToList();
            Equal(2, sent.Count, "two provider requests");
            foreach (var request in sent)
            {
                var wire = request.Uri + "\n" + string.Join("\n", request.Headers.Select(pair => pair.Key + ": " + pair.Value)) + "\n" + request.Body;
                // The metadata would be a "pisharp" key or a header named after it.
                Check(!request.Body.Contains("\"pisharp\"", StringComparison.OrdinalIgnoreCase), "no pisharp key in the body: " + request.Uri);
                Check(!request.Headers.Keys.Any(name => name.Contains("pisharp", StringComparison.OrdinalIgnoreCase)), "no pisharp header: " + request.Uri);
                Check(!wire.Contains("acct-secret-route", StringComparison.Ordinal), "no account: " + request.Uri);
                foreach (var op in ops) Check(!wire.Contains(op!, StringComparison.Ordinal), "no op: " + request.Uri);
            }
            var messages = sent.Single(request => request.Uri.Contains("anthropic", StringComparison.Ordinal));
            Check(messages.Body.Contains("\"user_id\":\"user-7\"", StringComparison.Ordinal), "anthropic still gets metadata.user_id: " + messages.Body);
        }),
    ];

    /// <summary>A hook's fields for <paramref name="account"/> on the default tier.</summary>
    private static JsonObjectValue Account(string account) => SessionStreamMetadata.Fields(("account", account), ("tier", "default"));

    /// <summary>The <c>streamOptions</c> the session's live generation task pinned in its <c>request</c> checkpoint.</summary>
    private static async Task<JsonObjectValue> PinnedStreamOptionsAsync(HostedSession session)
    {
        JsonObjectValue? found = null;
        await WaitUntilAsync(async () => (found = await PinnedOrNullAsync(session)) is not null, "a pinned request");
        return found!;
    }

    private static async Task<JsonObjectValue?> PinnedOrNullAsync(HostedSession session)
    {
        var page = await session.GetHarness().CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery(), 500, null), DurableContext.Background);
        return page.Items.Where(task => task.Kind == "pi.generation").Select(task => task.State).OfType<LiveTaskState>()
            .Select(state => state.Checkpoint).OfType<JsonObjectValue>()
            .Where(checkpoint => checkpoint.TryGetValue("phase", out var phase) && phase is JsonStringValue { Value: "request" })
            .Select(checkpoint => checkpoint.TryGetValue("streamOptions", out var options) ? options as JsonObjectValue : null)
            .FirstOrDefault(options => options is not null);
    }

    /// <summary>A faux model that records every request's options and holds <c>hold:</c> prompts until <see cref="Release"/>; held
    /// prompts answer <c>released</c>.</summary>
    private sealed class HeldModel
    {
        private readonly ConcurrentQueue<DurableStreamOptions?> requests = new();
        private readonly SemaphoreSlim arrived = new(0);
        private int taken;

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<DurableStreamOptions?> All => [.. requests];

        public FauxDurableModels Models() => new()
        {
            OnRequest = (_, options) =>
            {
                requests.Enqueue(options);
                arrived.Release();
            },
            RespondAsync = async (chat, cancellationToken) =>
            {
                if (!chat.Text.StartsWith("hold:", StringComparison.Ordinal)) return null;
                await Release.Task.WaitAsync(cancellationToken);
                return "released";
            },
        };

        /// <summary>A setup with a new model of this recorder and <paramref name="hook"/> as the stream hook (none when null).</summary>
        public DeviceSetup Setup(Func<string, JsonObjectValue?>? hook) => new()
        {
            Models = Models(),
            Configure = options => options with { SessionStream = hook },
        };

        /// <summary>The next request not yet taken, in arrival order.</summary>
        public async Task<DurableStreamOptions?> NextAsync()
        {
            if (!await arrived.WaitAsync(Answer)) throw new TimeoutException("No model request arrived.");
            return requests.ElementAt(taken++);
        }
    }
}
