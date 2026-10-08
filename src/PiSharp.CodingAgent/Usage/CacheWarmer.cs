// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/cache-warmer.ts.
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.AI.ModelOperations;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;

namespace PiSharp.CodingAgent.Usage;

/// <summary>settings-manager.ts <c>CACHE_WARMING_MODES</c> and <c>getCacheWarmingMode</c>: the global-only <c>cacheWarming</c>
/// setting, default <c>"streaming"</c>; any other stored value reads as the default.</summary>
public static class CacheWarmingModes
{
    public const string Off = "off", Streaming = "streaming", Idle = "idle", Default = Streaming;
    public static IReadOnlyList<string> All { get; } = [Off, Streaming, Idle];
    public static string Resolve(string? value) => value is not null && All.Contains(value) ? value : Default;
}

/// <summary>The <c>Model</c> fields cache warming reads: identity, <c>api</c>, <c>cost</c> (for <c>calculateCost</c>),
/// <c>promptCache</c> lifetimes in seconds and <c>compat.forceAdaptiveThinking</c>.</summary>
public sealed record CacheWarmModel(string Id, string Provider, string Api, ModelCost Cost, double? PromptCacheShortSeconds = null,
    double? PromptCacheLongSeconds = null, bool ForceAdaptiveThinking = false)
{
    /// <summary>Reads a Pi model object (catalog or <c>models.json</c> shape).</summary>
    public static CacheWarmModel FromJson(JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object) throw new FormatException("Model must be an object.");
        static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()! : throw new FormatException($"Model {name} must be a string.");
        static double? Seconds(JsonElement model, string name) => model.TryGetProperty("promptCache", out var cache) &&
            cache.ValueKind == JsonValueKind.Object && cache.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
        var adaptive = model.TryGetProperty("compat", out var compat) && compat.ValueKind == JsonValueKind.Object &&
            compat.TryGetProperty("forceAdaptiveThinking", out var force) && force.ValueKind == JsonValueKind.True;
        return new(Text(model, "id"), Text(model, "provider"), Text(model, "api"),
            model.TryGetProperty("cost", out var cost) ? ModelCost.FromJson(cost) : ModelCost.Free, Seconds(model, "short"), Seconds(model, "long"), adaptive);
    }
}

/// <summary>The <c>SimpleStreamOptions</c> fields cache warming reads. <see cref="Env"/> is the request's scoped provider
/// environment (<c>options.env</c>), consulted before the process environment.</summary>
public sealed record CacheWarmRequestOptions(string? CacheRetention = null, string? Reasoning = null, IReadOnlyDictionary<string, string?>? Env = null);

/// <summary>The request whose prompt cache entry should be kept warm, exactly as it was sent. <see cref="Payload"/> is the host's
/// own request (context and full stream options) that the replay delegate re-sends.</summary>
public sealed record CacheWarmRequest(CacheWarmModel Model, CacheWarmRequestOptions Options, object? Payload = null);

/// <summary>The overrides every refresh applies to the stored options: <c>maxTokens: 1, maxRetries: 0</c>.</summary>
public sealed record CacheWarmReplayOverrides(int MaxTokens, int MaxRetries);

/// <summary>Stands for <c>models.streamSimple(model, context, {...options, maxTokens: 1, maxRetries: 0, signal}).result()</c>.
/// The token is the run's own abort signal, cancelled when the run is replaced or stopped.</summary>
public delegate Task<AssistantMessage> CacheWarmReplay(CacheWarmRequest request, CacheWarmReplayOverrides overrides, CancellationToken signal);

/// <summary>Stands for <c>sessionManager.appendUsage(kind, provider, model, usage, note)</c>; returns the persisted usage entry.</summary>
public delegate Task<SessionEntry> CacheWarmUsageAppender(string kind, string provider, string model, TokenUsage usage, string? note);

/// <summary>Inputs and outcome of one warm-or-stop decision, as shown by <c>/session</c>.</summary>
/// <param name="Phase"><c>"streaming"</c> while the agent run that sent the request is still active, else <c>"idle"</c>.</param>
/// <param name="WarmCost">Price of this refresh: a cache read of the prompt plus one output token.</param>
/// <param name="MissCost">Extra price of the next real request if the cache entry is lost.</param>
/// <param name="ContinuationProbability">Estimated chance that a real request arrives before the entry expires.</param>
/// <param name="ExpectedSavings"><c>ContinuationProbability * MissCost - WarmCost</c>.</param>
/// <param name="EconomicsAvailable">False when the prompt size or the model's prices are unknown.</param>
/// <param name="Action">Pi's decision: <c>"warm"</c> when the expected savings are at least $0.05, else <c>"stop"</c>.</param>
public sealed record CacheWarmingDecision(string Phase, double WarmCost, double MissCost, double ContinuationProbability,
    double ExpectedSavings, bool EconomicsAvailable, string Action);

/// <summary>The <c>cache_warming_decision</c> extension event, fired before each refresh with pi's decision filled in.</summary>
public sealed record CacheWarmingDecisionEvent(double WarmCost, double MissCost, double ContinuationProbability, string Action)
{
    public string Type => "cache_warming_decision";
    public JsonData ToJson() => JsonData.Parse(JsonSerializer.Serialize(new
    { type = Type, warmCost = WarmCost, missCost = MissCost, continuationProbability = ContinuationProbability, action = Action }));
}

/// <param name="State"><c>"inactive"</c>, <c>"scheduled"</c> (a refresh timer is armed) or <c>"refreshing"</c> (a warm request is in flight).</param>
/// <param name="Reason">Why nothing is scheduled.</param>
/// <param name="NextWarmAt">Epoch milliseconds of the next decision.</param>
/// <param name="Decision">The pending decision, or the decision that stopped warming.</param>
/// <param name="ExtensionOverride">True when an extension changed <c>Decision.Action</c>.</param>
public sealed record CacheWarmingStatus(string State, string? Reason = null, double? NextWarmAt = null, CacheWarmingDecision? Decision = null,
    bool? ExtensionOverride = null);

/// <summary>
/// Keeps one prompt cache entry alive by re-sending its request with a one-token output cap before the entry expires.
/// <see cref="Start"/> replaces any previous run; warm requests never extend the fixed safety windows. Timers and clocks
/// come from the injected <see cref="TimeProvider"/>. State changes are serialized by a lock; the host delegates
/// (replay, append, decide, <see cref="OnWarmed"/>) run outside it, while <c>getMode</c>, <c>getBranch</c> and
/// <c>isCurrent</c> must be cheap synchronous reads.
/// </summary>
public sealed class CacheWarmer
{
    /// <summary>Streaming warming never continues past this long after the real request that started it.</summary>
    internal const double MaxWarmingAgeMs = 60 * 60_000;
    /// <summary>Idle warming uses a shorter horizon because continuation estimates become less reliable with age.</summary>
    internal const double MaxIdleWarmingAgeMs = 30 * 60_000;
    /// <summary>A refresh is sent only when it is expected to save at least this many dollars.</summary>
    public const double MinimumExpectedSavings = 0.05;
    /// <summary>Chance that a real request arrives before the cache entry expires while the agent sits idle.</summary>
    internal const double IdleContinuationProbability = 0.15;

    private sealed class ActiveRun(CacheWarmRequest request, Func<bool> isCurrent, double ttlMs, double delayMs, double startedAt)
    {
        internal readonly CacheWarmRequest Request = request;
        /// <summary>False once the session's model or messages no longer match the request.</summary>
        internal readonly Func<bool> IsCurrent = isCurrent;
        internal readonly double TtlMs = ttlMs, DelayMs = delayMs, StartedAt = startedAt;
        internal readonly CancellationTokenSource Controller = new();
        /// <summary>Latest safe time to send this refresh, leaving half the original expiry margin.</summary>
        internal double RefreshDeadlineAt;
        internal string Phase = "streaming";
        internal double NextWarmAt;
        /// <summary>Set while a refresh that an extension forced is in flight.</summary>
        internal bool ExtensionOverride;
        internal ITimer? Timer;
    }

    private readonly object _gate = new();
    private readonly CacheWarmReplay _replay;
    private readonly CacheWarmUsageAppender _appendUsage;
    private readonly Func<IReadOnlyList<SessionEntry>> _getBranch;
    private readonly Func<string> _getMode;
    private readonly Func<CacheWarmingDecisionEvent, Task<string>> _decide;
    private readonly TimeProvider _time;
    private readonly Func<string, string?> _processEnv;
    private ActiveRun? _run;
    private CacheWarmingStatus _inactive = new("inactive", "waiting for first request");

    /// <summary>Called with the persisted usage entry after each successful refresh.</summary>
    public Action<SessionEntry>? OnWarmed { get; set; }

    /// <summary>Test seam: the most recent timer-driven refresh.</summary>
    internal Task LastRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>Test seam: the active run, for driving <see cref="Refresh"/> as a late timer would.</summary>
    internal object? ActiveRunForTests { get { lock (_gate) return _run; } }

    /// <param name="replay">The stream call (<c>models.streamSimple</c>).</param>
    /// <param name="appendUsage">Persists the refresh usage (<c>sessionManager.appendUsage</c>).</param>
    /// <param name="getBranch">The session's current branch (<c>sessionManager.getBranch</c>).</param>
    /// <param name="getMode">The persisted <c>cacheWarming</c> mode.</param>
    /// <param name="decide">Lets extensions override <c>event.action</c>; failures fall back to pi's decision.</param>
    /// <param name="processEnvironment">The process environment for <c>PI_CACHE_RETENTION</c>; defaults to the real one.</param>
    public CacheWarmer(CacheWarmReplay replay, CacheWarmUsageAppender appendUsage, Func<IReadOnlyList<SessionEntry>> getBranch, Func<string> getMode,
        Func<CacheWarmingDecisionEvent, Task<string>>? decide = null, TimeProvider? timeProvider = null, Func<string, string?>? processEnvironment = null)
    {
        _replay = replay ?? throw new ArgumentNullException(nameof(replay));
        _appendUsage = appendUsage ?? throw new ArgumentNullException(nameof(appendUsage));
        _getBranch = getBranch ?? throw new ArgumentNullException(nameof(getBranch));
        _getMode = getMode ?? throw new ArgumentNullException(nameof(getMode));
        _decide = decide ?? (decisionEvent => Task.FromResult(decisionEvent.Action));
        _time = timeProvider ?? TimeProvider.System; _processEnv = processEnvironment ?? Environment.GetEnvironmentVariable;
    }

    private double Now => _time.GetUtcNow().ToUnixTimeMilliseconds();

    public CacheWarmingStatus Status
    {
        get
        {
            lock (_gate)
            {
                if (_getMode() == CacheWarmingModes.Off) return new("inactive", "cache warming disabled");
                var run = _run;
                if (run is null) return _inactive;
                if (!run.IsCurrent()) return new("inactive", "conversation context changed");
                var decision = Evaluate(run);
                var refreshing = run.Timer is null;
                if (!decision.EconomicsAvailable && !refreshing) return new("inactive", "cache economics unavailable");
                return new(refreshing ? "refreshing" : "scheduled", null, run.NextWarmAt, decision, run.ExtensionOverride);
            }
        }
    }

    /// <summary>Keep the prompt cache entry written by <paramref name="request"/> warm while <paramref name="isCurrent"/> holds.</summary>
    public void Start(CacheWarmRequest request, Func<bool> isCurrent)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(isCurrent);
        lock (_gate)
        {
            ClearRun();
            if (_getMode() == CacheWarmingModes.Off) { Stop("cache warming disabled"); return; }
            if (!IsReplayable(request.Model, request.Options)) { Stop("request cannot be replayed safely"); return; }
            var ttlMs = GetPromptCacheTtlMs(request.Model, request.Options, _processEnv);
            if (ttlMs is null)
            {
                Stop(request.Options.CacheRetention == "none" ? "request disabled prompt caching" : "cache lifetime unavailable");
                return;
            }
            if (GetCacheWarmingDelayMs(ttlMs.Value) is not { } delayMs) { Stop("cache lifetime unavailable"); return; }
            _run = new(request, isCurrent, ttlMs.Value, delayMs, Now);
            Schedule(_run);
        }
    }

    public void OnAgentSettled()
    {
        lock (_gate)
        {
            var run = _run;
            if (run is null) return;
            if (_getMode() == CacheWarmingModes.Streaming) { Stop("agent run settled"); return; }
            run.Phase = "idle";
            var deadline = run.StartedAt + MaxIdleWarmingAgeMs;
            if (run.NextWarmAt > deadline || Now >= deadline) Stop("30-minute idle safety limit reached");
        }
    }

    /// <summary>Reconcile an active run after the persisted warming mode changes.</summary>
    public void OnModeChanged()
    {
        lock (_gate)
        {
            var run = _run;
            if (run is null) return;
            if (GetModeStopReason(run) is { } reason) Stop(reason);
        }
    }

    public void Cancel() { lock (_gate) Stop("inactive"); }

    private void ClearRun()
    {
        var run = _run;
        if (run is null) return;
        _run = null;
        run.Timer?.Dispose();
        run.Controller.Cancel();
    }

    private void Stop(string reason, CacheWarmingDecision? decision = null, bool? extensionOverride = null)
    {
        ClearRun();
        _inactive = new("inactive", reason, null, decision, extensionOverride);
    }

    private void Schedule(ActiveRun run)
    {
        run.ExtensionOverride = false;
        run.NextWarmAt = Now + run.DelayMs;
        // A timer can run late after sleep or event-loop blockage. Keep half of the planned pre-expiry margin for that delay
        // and request dispatch; a late refresh is likely a full-price cache write, not a cache warm.
        run.RefreshDeadlineAt = run.NextWarmAt + Math.Floor((run.TtlMs - run.DelayMs) / 2);
        var deadline = run.StartedAt + (run.Phase == "idle" ? MaxIdleWarmingAgeMs : MaxWarmingAgeMs);
        if (run.NextWarmAt > deadline || Now >= deadline)
        {
            Stop(run.Phase == "idle" ? "30-minute idle safety limit reached" : "one-hour safety limit reached");
            return;
        }
        run.Timer = _time.CreateTimer(_ => LastRefresh = Refresh(run), null,
            TimeSpan.FromMilliseconds(Math.Max(0, run.NextWarmAt - Now)), Timeout.InfiniteTimeSpan);
    }

    /// <summary>One timer-driven refresh. Internal so tests can drive a late timer directly.</summary>
    internal async Task Refresh(object state)
    {
        var run = (ActiveRun)state;
        CacheWarmingDecision decision;
        lock (_gate)
        {
            run.Timer?.Dispose(); run.Timer = null;
            if (!ValidateRun(run) || RefreshDeadlineMissed(run)) return;
            decision = Evaluate(run);
        }
        var action = decision.Action;
        try { action = await _decide(new(decision.WarmCost, decision.MissCost, decision.ContinuationProbability, action)).ConfigureAwait(false); }
        catch (Exception) { /* Extension failures fall back to pi's own decision. */ }
        bool extensionOverride;
        lock (_gate)
        {
            if (!ValidateRun(run) || RefreshDeadlineMissed(run)) return;
            extensionOverride = action != decision.Action;
            if (action == "stop")
            {
                Stop(extensionOverride ? "stopped by extension" : decision.EconomicsAvailable ? "expected savings below threshold" : "cache economics unavailable",
                    decision, extensionOverride);
                return;
            }
            run.ExtensionOverride = extensionOverride;
        }
        try
        {
            var message = await _replay(run.Request, new(1, 0), run.Controller.Token).ConfigureAwait(false);
            bool valid; lock (_gate) valid = ValidateRun(run);
            if (!valid) return;
            if (message.StopReason is not (StopReason.Error or StopReason.Aborted))
            {
                var responseModel = message.ExtraProperties is { } extras && extras.TryGet("responseModel", out var value) && value is not null &&
                    value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
                var entry = await _appendUsage("cache_warm", message.Provider, responseModel ?? message.Model, message.Usage,
                    extensionOverride ? "extension override" : null).ConfigureAwait(false);
                OnWarmed?.Invoke(entry);
            }
        }
        catch (Exception) { /* Cache warming is best-effort and must not affect the active agent run. */ }
        lock (_gate) if (ReferenceEquals(_run, run)) Schedule(run);
    }

    private bool RefreshDeadlineMissed(ActiveRun run)
    {
        if (Now <= run.RefreshDeadlineAt) return false;
        Stop("cache refresh deadline missed");
        return true;
    }

    private bool ValidateRun(ActiveRun run)
    {
        if (!ReferenceEquals(_run, run)) return false;
        var reason = GetModeStopReason(run) ?? (!run.IsCurrent() ? "conversation context changed" : null);
        if (reason is null) return true;
        Stop(reason);
        return false;
    }

    private string? GetModeStopReason(ActiveRun run)
    {
        var mode = _getMode();
        if (mode == CacheWarmingModes.Off) return "cache warming disabled";
        if (mode == CacheWarmingModes.Streaming && run.Phase == "idle") return "agent run settled";
        return null;
    }

    private CacheWarmingDecision Evaluate(ActiveRun run)
    {
        var model = run.Request.Model;
        var promptTokens = LastPromptTokens(_getBranch());
        var cacheHitCost = model.Cost.Calculate(0, 0, promptTokens, 0).Total;
        var cacheMissCost = model.Cost.CacheWrite > 0 ? model.Cost.Calculate(0, 0, 0, promptTokens).Total : model.Cost.Calculate(promptTokens, 0, 0, 0).Total;
        var warmCost = model.Cost.Calculate(0, 1, promptTokens, 0).Total;
        var missCost = Math.Max(0, cacheMissCost - cacheHitCost);
        var continuationProbability = run.Phase == "idle" ? IdleContinuationProbability : 1;
        var economicsAvailable = promptTokens > 0 && (cacheHitCost > 0 || cacheMissCost > 0);
        var expectedSavings = continuationProbability * missCost - warmCost;
        return new(run.Phase, warmCost, missCost, continuationProbability, expectedSavings, economicsAvailable,
            expectedSavings >= MinimumExpectedSavings ? "warm" : "stop");
    }

    /// <summary>Prompt size of the most recent real request on the branch, as reported by the provider.</summary>
    private static double LastPromptTokens(IReadOnlyList<SessionEntry> entries)
    {
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var entry = entries[index];
            if (entry.Kind == SessionEntryKind.Message && entry.WireBody.Value.TryGetProperty("message", out var message) &&
                UsageWire.OptionalString(message, "role") == "assistant")
            {
                var usage = message.TryGetProperty("usage", out var value) ? value : default;
                return UsageWire.Number(usage, "input") + UsageWire.Number(usage, "cacheRead") + UsageWire.Number(usage, "cacheWrite");
            }
        }
        return 0;
    }

    /// <summary>Refresh at 90% of the TTL while preserving at least ten seconds of margin.</summary>
    public static double? GetCacheWarmingDelayMs(double ttlMs)
    {
        if (ttlMs <= 10_000) return null;
        return Math.Max(1, Math.Floor(Math.Min(ttlMs * 0.9, ttlMs - 10_000)));
    }

    /// <summary>Lifetime of the prompt cache entry a request writes, from the model's <c>promptCache</c> tier for the retention the
    /// request used (<c>options.cacheRetention</c>, else <c>PI_CACHE_RETENTION=long</c> from the scoped then the process
    /// environment, else short). Null when the model has no lifetime for that tier or caching is off.</summary>
    public static double? GetPromptCacheTtlMs(CacheWarmModel model, CacheWarmRequestOptions? options, Func<string, string?>? processEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var retention = options?.CacheRetention ?? (ProviderEnv("PI_CACHE_RETENTION", options?.Env, processEnvironment) == "long" ? "long" : "short");
        if (retention == "none") return null;
        var seconds = retention switch { "short" => model.PromptCacheShortSeconds, "long" => model.PromptCacheLongSeconds, _ => null };
        return seconds is null ? null : seconds.Value * 1000;
    }

    /// <summary>Whether replaying the request with a one-token output cap leaves its cache entry untouched. Anthropic's
    /// budget-based thinking (Claude models without adaptive thinking) derives <c>budget_tokens</c> from <c>max_tokens</c>; the
    /// replay would get a different budget, which Anthropic keys the message cache on.</summary>
    public static bool IsReplayable(CacheWarmModel model, CacheWarmRequestOptions? options)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (string.IsNullOrEmpty(options?.Reasoning) || model.Api != "anthropic-messages") return true;
        return model.ForceAdaptiveThinking;
    }

    /// <summary>provider-env.ts <c>getProviderEnvValue</c>: a non-empty scoped value, else a non-empty process value.</summary>
    private static string? ProviderEnv(string name, IReadOnlyDictionary<string, string?>? scoped, Func<string, string?>? process)
    {
        if (scoped is not null && scoped.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value)) return value;
        var fromProcess = (process ?? Environment.GetEnvironmentVariable)(name);
        return string.IsNullOrEmpty(fromProcess) ? null : fromProcess;
    }

    private static string FormatDollars(double value) =>
        value < 0 ? "-$" + ToFixed(Math.Abs(value), 3) : "$" + ToFixed(value, 3);

    private static string FormatEconomics(CacheWarmingDecision decision)
    {
        if (!decision.EconomicsAvailable) return "cache economics unavailable";
        var probability = Math.Floor(decision.ContinuationProbability * 100 + 0.5).ToString(CultureInfo.InvariantCulture);
        var probabilityText = decision.Phase == "streaming"
            ? $"{probability}% continuation probability while agent is running" : $"{probability}% continuation probability";
        var comparison = decision.Action == "warm" ? ">=" : "<";
        return $"{probabilityText}, expected savings {FormatDollars(decision.ExpectedSavings)} {comparison} ${ToFixed(MinimumExpectedSavings, 3)}";
    }

    private static string FormatDecisionTime(double? nextWarmAt, double now)
    {
        if (nextWarmAt is null || nextWarmAt <= now) return "Decision now";
        var remainingSeconds = Math.Ceiling((nextWarmAt.Value - now) / 1000);
        var hours = Math.Floor(remainingSeconds / 3600);
        remainingSeconds %= 3600;
        var minutes = Math.Floor(remainingSeconds / 60);
        var seconds = remainingSeconds % 60;
        var parts = new List<string>();
        if (hours > 0) parts.Add($"{hours.ToString(CultureInfo.InvariantCulture)}h");
        if (minutes > 0) parts.Add($"{minutes.ToString(CultureInfo.InvariantCulture)}m");
        if (seconds > 0 || parts.Count == 0) parts.Add($"{seconds.ToString(CultureInfo.InvariantCulture)}s");
        return "Decision in " + string.Join(' ', parts);
    }

    /// <summary>One-line status for <c>/session</c>. <paramref name="now"/> is epoch milliseconds (default: the system clock).</summary>
    public static string FormatCacheWarmingStatus(CacheWarmingStatus status, double? now = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        var decision = status.Decision;
        // A decision is attached once pi (or an extension) acted on it; "inactive" without one never got that far.
        if (decision is null || (status.State == "inactive" && !decision.EconomicsAvailable && status.ExtensionOverride != true))
            return $"Inactive ({status.Reason ?? "unknown reason"})";
        var details = status.ExtensionOverride == true
            ? $"extension override, {FormatEconomics(decision)}" : $"{FormatEconomics(decision)} -> {decision.Action}";
        if (status.State == "inactive") return $"Stopped ({details})";
        if (status.State == "refreshing") return $"Warming cache ({details})";
        return $"{FormatDecisionTime(status.NextWarmAt, now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())} ({details})";
    }

    /// <summary>One-line transcript text for a persisted cache-warming usage entry.</summary>
    public static string FormatCacheWarmingUsage(SessionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var body = entry.WireBody.Value;
        var noteValue = UsageWire.OptionalString(body, "note");
        var note = string.IsNullOrEmpty(noteValue) ? "" : $" ({noteValue})";
        var cost = Regex.Replace(ToFixed(UsageWire.CostTotal(body.GetProperty("usage")), 6), @"(\.\d{3}\d*?)0+$", "$1", RegexOptions.ECMAScript);
        return $"Cache warmed{note}: ${cost}";
    }

    /// <summary><c>Number.prototype.toFixed</c> for finite values below 1e21.</summary>
    internal static string ToFixed(double value, int digits) =>
        (value == 0 ? 0 : value).ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
