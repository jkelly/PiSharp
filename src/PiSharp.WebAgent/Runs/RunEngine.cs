// Decision 0011 (run W4, R0): runs one provider call for the Playground, Compare, Bench and agent compare pages. A RunSpec is checked
// (RunSpec.Normalize: what no model allows) and planned (RunModelOptions.Plan: what this model and its API take; an option the user set
// that the model does not take refuses the run with the reason, nothing is sent). The call is reserved with the Spend Guard first
// (GuardedRunExecutor; a Compare run reserves its columns as one group, all or none): a refused call answers {posted: false} with the
// guard's decision and sends nothing (the user's choice in the confirm modal comes back as a one-time approval of the run, stored before
// it is reserved again). A reserved call gets its runs.db row (running) and runs in the background, tied to the app's life rather than to
// the request, so it keeps going when the tab closes: through AccountRouter (WebDurableModels' host transport) with the run's account, or,
// with --faux, through the faux models offline (--faux-metered prices them). The guard's output cap ("cap the output") is sent as max
// tokens. Every normalized event is published to the run's live log (RunLive, for /api/runs/{id}/events) with its time, and stored with the
// row when event storage is on; raw payloads are captured (redacted) by the routing hook only when the user turned them on. When the call
// ends the row records the output, usage, cost (the ledger row it settled: actual, estimate, uncertain or released, with its billing
// mode), time to first token, duration, tokens per second, stop reason and error. A cancelled run ends aborted; a run the app stops during
// ends interrupted, and one the app was killed during is marked interrupted at the next start (RunStore): neither is retried by itself.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Hosting;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Spend;
using ChordObject = PiSharp.Durable.Chord.JsonObjectValue;

namespace PiSharp.WebAgent.Runs;

/// <summary>The user's answer to the Spend Guard that a run request carries (<c>spend: {choice, maxOutputTokens?}</c>).</summary>
/// <param name="Choice">Proceed once or cap the output.</param>
/// <param name="MaxOutputTokens">The output cap, for <see cref="SpendChoice.CapOutput"/>.</param>
public sealed record RunSpendAnswer(SpendChoice Choice, int? MaxOutputTokens = null)
{
    /// <summary>The answer in a request's <c>spend</c> field: null for none (or <c>{check: true}</c>, which every run gets anyway).</summary>
    /// <exception cref="ArgumentException">The choice or output cap is not allowed.</exception>
    public static RunSpendAnswer? Read(JsonNode? spend)
    {
        if (spend is null) return null;
        if (spend is not JsonObject body) throw new ArgumentException("spend is {choice: proceed-once|cap-output, maxOutputTokens?}.");
        var choice = body["choice"] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : null;
        long? tokens = body["maxOutputTokens"] is JsonValue number && number.TryGetValue<long>(out var count) ? count : null;
        return choice switch
        {
            null or "" or "check" => null,
            "proceed-once" => new RunSpendAnswer(SpendChoice.ProceedOnce),
            "cap-output" when tokens is >= 1 and <= int.MaxValue => new RunSpendAnswer(SpendChoice.CapOutput, (int)tokens),
            "cap-output" => throw new ArgumentException("Capping the output needs maxOutputTokens of 1 or more."),
            _ => throw new ArgumentException($"\"{choice}\" is not a spend choice: proceed-once or cap-output."),
        };
    }
}

/// <summary>Why the Spend Guard did not let a run through; nothing was sent.</summary>
/// <param name="Code">The spend error code (<c>spend_ask</c>, <c>spend_blocked</c>, <c>spend_unknown_price</c>).</param>
/// <param name="Message">Why, in a sentence.</param>
/// <param name="Call">The call as checked (for the confirm modal).</param>
/// <param name="Decision">The guard's decision.</param>
public sealed record RunRefusal(string Code, string Message, SpendCall Call, SpendDecision Decision);

/// <summary>What a run request started.</summary>
/// <param name="Posted">Whether anything started.</param>
/// <param name="Ids">The runs started (one per column for a group).</param>
/// <param name="GroupId">The group, for a group.</param>
/// <param name="Refusal">Why nothing started, when the Spend Guard refused it.</param>
public sealed record RunStart(bool Posted, IReadOnlyList<string> Ids, string? GroupId, RunRefusal? Refusal)
{
    /// <summary>The first run's id.</summary>
    public string? Id => Ids.Count > 0 ? Ids[0] : null;
}

/// <summary>Runs provider calls for the pages. A singleton.</summary>
public sealed class RunEngine : IAsyncDisposable
{
    /// <summary>The most columns a group runs.</summary>
    public const int MaxGroup = 16;

    /// <summary>The most events stored per run.</summary>
    public const int MaxStoredEvents = 50_000;

    /// <summary>The most streamed text kept for a run that stopped before its final message.</summary>
    public const int MaxPartialChars = 1_000_000;

    private readonly RunStore store;
    private readonly RunLive live;
    private readonly IServiceProvider services;
    private readonly ILogger log;
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<string, Work> active = new(StringComparer.Ordinal);
    private readonly Lazy<IDurableModels> models;
    private int disposed;

    /// <summary>An engine over the app's services (the final IDurableModels, the Spend Guard's executor, the settings and accounts).</summary>
    public RunEngine(RunStore store, RunLive live, IServiceProvider services, ILogger<RunEngine>? log = null, IHostApplicationLifetime? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(services);
        this.store = store;
        this.live = live;
        this.services = services;
        this.log = (ILogger?)log ?? NullLogger.Instance;
        models = new Lazy<IDurableModels>(() => RunModels.Unguarded(services.GetRequiredService<IDurableModels>()));
        lifetime?.ApplicationStopping.Register(() => stopping.Cancel());
    }

    /// <summary>The models a run streams through: the app's final models without the Spend Guard and the recorder (the engine reserves and
    /// records itself): the routed models, or the (priced) faux ones with --faux.</summary>
    public IDurableModels Models => models.Value;

    /// <summary>What <paramref name="provider"/>/<paramref name="model"/> takes, or null when it is not a model.</summary>
    public RunCapabilities? Capabilities(string provider, string model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);
        return Models.GetModel(provider.Trim(), model.Trim()) is { } found ? RunModelOptions.Capabilities(found, Catalog(found)) : null;
    }

    /// <summary>The request <paramref name="spec"/> would send (for View code, and the check before a run): normalized, with what is
    /// applied and what is refused.</summary>
    /// <exception cref="ArgumentException">The spec is not valid, or the model is unknown.</exception>
    public RunRequestPlan Plan(RunSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var normalized = spec.Normalize();
        if (normalized.RequestBytes() > RunSpec.MaxRequestBytes) throw new ArgumentException($"The request is over {RunSpec.MaxRequestBytes / (1024 * 1024)} MB.");
        var model = Models.GetModel(normalized.Provider, normalized.Model)
            ?? throw new ArgumentException($"There is no model {normalized.Provider}/{normalized.Model}.");
        return RunModelOptions.Plan(normalized, model, Catalog(model), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>Starts one run: checked, planned, reserved, then running in the background.</summary>
    /// <exception cref="ArgumentException">The spec is not valid (<see cref="RunOptionException"/> for options the model does not take), the
    /// model or account is unknown, or the spend answer is not allowed.</exception>
    public async Task<RunStart> StartAsync(RunSpec spec, RunSpendAnswer? spend = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var plan = Plan(spec);
        if (!plan.Allowed) throw new RunOptionException(plan);
        var id = NewId();
        var account = await AccountAsync(plan, cancellationToken).ConfigureAwait(false);
        var call = Call(plan, id, account, plan.Spec.GroupId ?? id);
        var executor = services.GetRequiredService<GuardedRunExecutor>();
        if (spend is not null)
            await executor.Guard.ApproveStoredAsync(new SpendApproval(call.Run!, spend.Choice) { MaxOutputTokens = spend.MaxOutputTokens, Op = call.Op }.Normalize(),
                cancellationToken).ConfigureAwait(false);
        var lease = await executor.ReserveAsync(call, cancellationToken).ConfigureAwait(false);
        if (!lease.Reserved)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            return new RunStart(false, [], null, Refusal(call, lease.Decision));
        }
        Begin(id, plan, account, call, lease, plan.Spec.GroupId);
        return new RunStart(true, [id], plan.Spec.GroupId, null);
    }

    /// <summary>Starts a group (a Compare run's columns) reserved all or none; each column is its own run of the group.</summary>
    /// <exception cref="ArgumentException">A spec is not valid (the message names its column), or the group is empty or too large.</exception>
    public async Task<RunStart> StartGroupAsync(IReadOnlyList<RunSpec> specs, string? groupId = null, RunSpendAnswer? spend = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(specs);
        if (specs.Count == 0) throw new ArgumentException("Add at least one column.");
        if (specs.Count > MaxGroup) throw new ArgumentException($"A group runs at most {MaxGroup} columns.");
        var group = string.IsNullOrWhiteSpace(groupId) ? "g" + Guid.NewGuid().ToString("N") : groupId.Trim();
        if (group.Length > 100 || group.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new ArgumentException("A group id is letters, digits, '-' and '_' (at most 100).");
        var plans = new List<RunRequestPlan>();
        for (var index = 0; index < specs.Count; index++)
        {
            RunRequestPlan plan;
            try
            {
                plan = Plan(specs[index] with { GroupId = group, Source = specs[index].Source ?? SpendSource.Compare.Wire() });
            }
            catch (ArgumentException error)
            {
                throw new ArgumentException($"Column {index + 1}: {error.Message}", error);
            }
            if (!plan.Allowed) throw new RunOptionException(plan);
            plans.Add(plan);
        }
        var ids = plans.Select(_ => NewId()).ToList();
        var accounts = new List<string?>();
        foreach (var plan in plans) accounts.Add(await AccountAsync(plan, cancellationToken).ConfigureAwait(false));
        var calls = plans.Select((plan, index) => Call(plan, ids[index], accounts[index], group)).ToList();
        var executor = services.GetRequiredService<GuardedRunExecutor>();
        if (spend is not null)
            await executor.Guard.ApproveStoredAsync(new SpendApproval(group, spend.Choice) { MaxOutputTokens = spend.MaxOutputTokens }.Normalize(),
                cancellationToken).ConfigureAwait(false);
        var leases = await executor.ReserveGroupAsync(calls, cancellationToken).ConfigureAwait(false);
        if (!leases.Reserved)
        {
            await leases.DisposeAsync().ConfigureAwait(false);
            var refusing = leases.Reservation.Items.FirstOrDefault(item => !item.Reserved);
            var decision = leases.Reservation.Refused ?? refusing?.GroupDecision ?? refusing?.Decision ?? leases.Leases[0].Decision;
            return new RunStart(false, [], group, Refusal(refusing?.Call ?? calls[0], decision));
        }
        try
        {
            for (var index = 0; index < plans.Count; index++) Begin(ids[index], plans[index], accounts[index], calls[index], leases.Leases[index], group);
        }
        catch
        {
            // The columns not started yet are released; the started ones run and settle on their own.
            await leases.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return new RunStart(true, ids, group, null);
    }

    /// <summary>Cancels the running run <paramref name="id"/> (it ends aborted).</summary>
    /// <returns>Whether it was running here.</returns>
    public bool Cancel(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!active.TryGetValue(id, out var work)) return false;
        try { work.Cancel.Cancel(); }
        catch (ObjectDisposedException) { return false; }
        return true;
    }

    /// <summary>The task of the running run <paramref name="id"/>, or null when it is not running here (for tests and shutdown).</summary>
    public Task? Completion(string id) => active.TryGetValue(id, out var work) ? work.Task : null;

    /// <summary>Stops every run (they end interrupted) and waits up to 10 seconds for their rows.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await stopping.CancelAsync().ConfigureAwait(false);
        var tasks = active.Values.Select(work => work.Task).Where(task => task is not null).Cast<Task>().ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException) { log.LogWarning("Runs were still ending when the app stopped."); }
        stopping.Dispose();
    }

    private static string NewId() => "r" + Guid.NewGuid().ToString("N");

    private CatalogModel? Catalog(DurableModel model)
    {
        try { return services.GetService<IModelCatalog>()?.FindModel(model.Provider, model.Id); }
        catch (Exception error) when (error is not OperationCanceledException) { return null; }
    }

    private async Task<string?> AccountAsync(RunRequestPlan plan, CancellationToken cancellationToken)
    {
        var spec = plan.Spec;
        if (plan.Model.Api == RunModelOptions.FauxApi) return null;
        var accounts = services.GetService<IChatAccounts>();
        if (spec.Account is { } id)
        {
            if (accounts is null) return id;
            return (await accounts.FindAsync(spec.Provider, id, cancellationToken).ConfigureAwait(false))?.Id
                ?? throw new ArgumentException($"There is no {spec.Provider} account {id}; choose another in Settings › Accounts.");
        }
        return accounts is null ? null : (await accounts.BindAsync(spec.Provider, cancellationToken).ConfigureAwait(false))?.Id;
    }

    private static SpendCall Call(RunRequestPlan plan, string id, string? account, string run) => new()
    {
        Provider = plan.Model.Provider,
        Model = plan.Model.Id,
        Account = account,
        Source = plan.Spec.SpendSource,
        Run = run,
        Op = "run-" + id,
        InputBytes = CostEstimator.InputBytes(plan.Messages),
        MaxTokens = plan.Spec.MaxTokens,
    };

    private static RunRefusal Refusal(SpendCall call, SpendDecision decision) =>
        new(decision.ErrorCode ?? SpendErrors.Ask, SpendErrors.Describe(decision), call, decision);

    private void Begin(string id, RunRequestPlan plan, string? account, SpendCall call, GuardedLease lease, string? group)
    {
        var now = DateTimeOffset.UtcNow;
        var spec = plan.Spec;
        var options = spec.OptionsJson();
        options["applied"] = new JsonArray([.. plan.Applied.Select(option => (JsonNode)JsonValue.Create(option)!)]);
        options["inputBytes"] = call.InputBytes;
        var record = new RunRecord
        {
            Id = id,
            Op = call.Op,
            Source = spec.SpendSource.Wire(),
            Status = RunStatus.Running,
            Provider = plan.Model.Provider,
            Model = plan.Model.Id,
            Api = plan.Model.Api,
            Account = account,
            GroupId = group,
            ParentId = spec.ParentId,
            Title = spec.Title ?? RunText.Title(spec.FirstUserText()),
            Options = options,
            Messages = spec.MessagesJson(),
            SpendOp = lease.LedgerOp,
            Notes = plan.Notes.Count > 0 ? string.Join(" ", plan.Notes) : null,
            Created = now,
            Started = now,
            Updated = now,
        };
        // The live log opens first, so a page that connects as soon as the row exists finds the run live.
        var liveRun = live.Open(id);
        try
        {
            store.Insert(record);
        }
        catch (Exception error)
        {
            liveRun.End(new JsonObject { ["id"] = id, ["status"] = RunStatus.Error, ["error"] = "The run could not be recorded, so it was not sent." });
            _ = lease.ReleaseAsync("the run could not be recorded");
            log.LogError(error, "Run {Run} could not be recorded; it was not sent.", id);
            throw;
        }
        var work = new Work(id, plan, account, call, lease, liveRun, CancellationTokenSource.CreateLinkedTokenSource(stopping.Token), Stopwatch.StartNew());
        active[id] = work;
        // The run belongs to the app, not to the request that started it: nothing of the request's context flows into it.
        using (ExecutionContext.SuppressFlow()) work.Task = Task.Run(() => ExecuteAsync(work));
    }

    private async Task ExecuteAsync(Work work)
    {
        var plan = work.Plan;
        var model = plan.Model;
        var settings = Settings();
        var offline = model.Api == RunModelOptions.FauxApi;
        var capture = settings.StoreRawPayloads && !offline ? new RunCapture(() => work.Clock.ElapsedMilliseconds) : null;
        var http = new RunHttpContext
        {
            RunId = work.Id, Api = model.Api, Patch = plan.Patch, PatchPath = plan.PatchPath, Headers = plan.Headers, StrictTools = plan.StrictTools,
            TimeoutMs = offline ? null : plan.Spec.TimeoutMs, Capture = capture,
        };
        var events = new List<RunEventRecord>();
        var partial = new StringBuilder();
        StreamTerminalEvent? terminal = null;
        long? ttft = null;
        string? failure = null;
        try
        {
            RunHttpScope.Current = http;
            await foreach (var item in work.Lease.StreamAsync(model.Descriptor,
                (send, token) => Models.StreamSimple(model, plan.Messages, Options(work, send), token), work.Cancel.Token).ConfigureAwait(false))
            {
                var at = work.Clock.ElapsedMilliseconds;
                if (ttft is null && IsContent(item)) ttft = at;
                var json = JsonNode.Parse(PiWireJson.WriteEvent(item).ToString())!;
                var seq = work.Live.Publish(at, json);
                if (settings.StoreSseEvents && events.Count < MaxStoredEvents) events.Add(new RunEventRecord(seq, at, json));
                if (item is TextDelta delta && partial.Length < MaxPartialChars) partial.Append(delta.Delta);
                if (item is StreamTerminalEvent end) terminal = end;
            }
        }
        catch (OperationCanceledException) when (work.Cancel.IsCancellationRequested)
        {
            failure = null;
        }
        catch (Exception error)
        {
            failure = error.Message;
            log.LogWarning("Run {Run} ({Provider}/{Model}) failed: {Error}", work.Id, model.Provider, model.Id, error.Message);
        }
        finally
        {
            RunHttpScope.Current = null;
        }
        var duration = work.Clock.ElapsedMilliseconds;
        var outcome = Outcome(work, terminal, failure, ttft, duration, http, capture, events, partial);
        try
        {
            store.Finish(work.Id, outcome);
        }
        catch (Exception error)
        {
            log.LogError(error, "Run {Run} ended but its row could not be stored.", work.Id);
        }
        finally
        {
            work.Live.End(Status(work.Id, outcome));
            active.TryRemove(KeyValuePair.Create(work.Id, work));
            work.Cancel.Dispose();
        }
    }

    private RunOutcome Outcome(Work work, StreamTerminalEvent? terminal, string? failure, long? ttft, long duration, RunHttpContext http,
        RunCapture? capture, List<RunEventRecord> events, StringBuilder partial)
    {
        var message = terminal?.Message;
        var stopped = work.Cancel.IsCancellationRequested;
        var interrupted = stopped && stopping.IsCancellationRequested;
        string status;
        string? error;
        if (message is null)
        {
            status = stopped ? interrupted ? RunStatus.Interrupted : RunStatus.Aborted : RunStatus.Error;
            error = stopped ? interrupted ? RunStore.InterruptedMessage : null : failure ?? "The stream ended without a final message.";
        }
        else if (terminal is StreamDone)
        {
            status = RunStatus.Completed;
            error = null;
        }
        else if (message.StopReason == StopReason.Aborted || stopped)
        {
            status = interrupted ? RunStatus.Interrupted : RunStatus.Aborted;
            error = interrupted ? RunStore.InterruptedMessage : null;
        }
        else
        {
            status = RunStatus.Error;
            error = ErrorText(message) ?? failure ?? "The provider ended the answer with an error.";
        }
        var output = message is null ? null : JsonNode.Parse(PiWireJson.WriteMessage(message).ToString());
        var usage = message?.Usage is { } reported
            ? new RunUsage(reported.Input, reported.Output, reported.CacheRead, reported.CacheWrite, reported.TotalTokens) : RunUsage.Zero;
        var (cost, costKind, billing) = Cost(work.Lease.Settled, message);
        double? rate = usage.Output > 0 && ttft is { } first && duration > first ? usage.Output / ((duration - first) / 1000.0) : null;
        var notes = new List<string>(work.Plan.Notes);
        if ((work.Plan.Patch is not null || work.Plan.StrictTools is not null) && http.Requests > 0 && http.Patched == 0)
            notes.Add("PiSharp did not find the model request to apply this run's options to, so it was sent without them.");
        if (capture?.Truncated == true) notes.Add($"Raw payloads were cut at {RunCapture.MaxBytes / (1024 * 1024)} MB.");
        if (work.Lease.Reservation.OutputCapped) notes.Add($"The output was capped at {work.Lease.Reservation.MaxTokens:N0} tokens (your spend choice).");
        return new RunOutcome
        {
            Status = status,
            Output = output,
            // A run stopped before its final message keeps the text it had streamed.
            OutputText = message is null ? partial.Length > 0 ? partial.ToString() : null : string.Concat(message.Content.OfType<TextContent>().Select(text => text.Text)),
            StopReason = output?["stopReason"] is JsonValue stop && stop.TryGetValue<string>(out var reason) ? reason : null,
            Error = RunText.Cut(error, 4000),
            Notes = notes.Count > 0 ? string.Join(" ", notes) : null,
            Usage = usage,
            CostMicros = cost,
            CostKind = costKind,
            Billing = billing,
            SpendOp = work.Lease.LedgerOp,
            TtftMs = ttft,
            DurationMs = duration,
            TokensPerSecond = rate is { } value && double.IsFinite(value) ? Math.Round(value, 2) : null,
            Finished = DateTimeOffset.UtcNow,
            Events = events,
            Raw = capture?.Items() ?? [],
        };
    }

    /// <summary>The cost of a call from the ledger row it settled (or the provider's reported cost when there is none).</summary>
    internal static (long? Micros, string? Kind, string? Billing) Cost(Reservation? row, AssistantMessage? message)
    {
        if (row is null)
        {
            var total = message?.Usage.Cost.Total ?? 0m;
            return total > 0 ? ((long)decimal.Round(total * 1_000_000m), "usage", null) : (null, null, null);
        }
        var billing = row.Billing switch
        {
            BillingKind.Metered => "metered",
            BillingKind.Subscription => "subscription",
            BillingKind.Free => "free",
            _ => "unknown",
        };
        return row.State switch
        {
            ReservationState.Settled => (row.ActualMicros ?? row.EstimateMicros, row.Settlement switch
            {
                SettlementKind.Estimate => "estimate",
                SettlementKind.Entered => "entered",
                _ => "actual",
            }, billing),
            ReservationState.Uncertain => (row.ReservedMicros, "uncertain", billing),
            ReservationState.Released => (0, "released", billing),
            _ => (row.ReservedMicros, "reserved", billing),
        };
    }

    /// <summary>The final status frame of a run (its row's outcome, without events and raw payloads).</summary>
    internal static JsonObject Status(string id, RunOutcome outcome) => new()
    {
        ["id"] = id,
        ["status"] = outcome.Status,
        ["stopReason"] = outcome.StopReason,
        ["error"] = outcome.Error,
        ["notes"] = outcome.Notes,
        ["usage"] = new JsonObject
        {
            ["input"] = outcome.Usage.Input, ["output"] = outcome.Usage.Output, ["cacheRead"] = outcome.Usage.CacheRead,
            ["cacheWrite"] = outcome.Usage.CacheWrite, ["total"] = outcome.Usage.Total,
        },
        ["costMicros"] = outcome.CostMicros,
        ["costKind"] = outcome.CostKind,
        ["billing"] = outcome.Billing,
        ["spendOp"] = outcome.SpendOp,
        ["ttftMs"] = outcome.TtftMs,
        ["durationMs"] = outcome.DurationMs,
        ["tokensPerSecond"] = outcome.TokensPerSecond,
    };

    private RunHistorySettings Settings()
    {
        try { return services.GetService<ISettingsStore>()?.Get<RunHistorySettings>() ?? RunHistorySettings.CreateDefault(); }
        catch (Exception error) when (error is not OperationCanceledException) { return RunHistorySettings.CreateDefault(); }
    }

    private static DurableStreamOptions Options(Work work, GuardedSend send) => new()
    {
        Metadata = ChordObject.Empty.SetItem(SessionStreamMetadata.Key, SessionStreamMetadata.Fields((SpendMetadata.AccountField, work.Account))
            .SetItem(SessionStreamMetadata.OperationField, work.Call.Op)
            .SetItem(SpendMetadata.SourceField, work.Call.Source.Wire())
            .SetItem(SpendMetadata.RunField, work.Call.Run ?? work.Id)),
        MaxTokens = send.MaxTokens,
        Reasoning = work.Plan.Thinking,
    };

    private static bool IsContent(StreamEvent item) => item is TextDelta or ThinkingDelta or ToolCallStarted or ToolCallProvisionalStarted or ToolCallDelta;

    private static string? ErrorText(AssistantMessage message) =>
        message.ExtraProperties is { } extra && extra.TryGet("errorMessage", out var value) && value is { Value.ValueKind: System.Text.Json.JsonValueKind.String }
            ? value.Value.GetString() : null;

    private sealed class Work(string id, RunRequestPlan plan, string? account, SpendCall call, GuardedLease lease, RunLiveRun live,
        CancellationTokenSource cancel, Stopwatch clock)
    {
        public string Id { get; } = id;
        public RunRequestPlan Plan { get; } = plan;
        public string? Account { get; } = account;
        public SpendCall Call { get; } = call;
        public GuardedLease Lease { get; } = lease;
        public RunLiveRun Live { get; } = live;
        public CancellationTokenSource Cancel { get; } = cancel;
        public Stopwatch Clock { get; } = clock;
        public Task? Task { get; set; }
    }
}
