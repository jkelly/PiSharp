// PiSharp-only (decision 0008). The window-based defaults adapt durable-pi's defaultCompaction (apps/dpi-sdk/src/api.ts, MIT): a background
// summary at 200k tokens and a blocking one at 250k, or pi-durable's margins on smaller windows. keepRecentTokens is pi-durable's 20000;
// durable-pi's SDK says 2000.
using System.Text.Json;

namespace PiSharp.Durable.Hosting;

/// <summary>
/// One session's compaction settings, applied to every agent of the session from its next request: the next request waits for a summary at
/// <c>window − ReserveTokens</c>, and a summary starts in the background <see cref="BackgroundTokens"/> earlier (0 disables background
/// compaction).
/// </summary>
public sealed record SessionCompaction(bool Enabled, double ReserveTokens, double BackgroundTokens, double KeepRecentTokens)
{
    /// <summary>pi-durable's <c>keepRecentTokens</c> default.</summary>
    public const double DefaultKeepRecentTokens = 20000;

    /// <summary>pi-durable's own margins: reserve 16384 tokens, start in the background 32768 earlier.</summary>
    public static SessionCompaction PiDurableDefaults { get; } = new(true, 16384, 32768, DefaultKeepRecentTokens);

    /// <summary>The smallest room a window must keep below the blocking threshold.</summary>
    public const double MinimumUsableTokens = 32768;

    /// <summary>The defaults for a model window: wait at <c>min(250000, window − 16384)</c> and compact in the background at
    /// <c>min(200000, wait − 32768)</c>. A window too small for that, or unknown (0), gets pi-durable's margins.</summary>
    public static SessionCompaction DefaultFor(double contextWindow)
    {
        if (!double.IsFinite(contextWindow) || contextWindow <= 16384 + 32768 + MinimumUsableTokens) return PiDurableDefaults;
        var wait = Math.Min(250_000, contextWindow - 16384);
        var auto = Math.Min(200_000, wait - 32768);
        return new SessionCompaction(true, contextWindow - wait, wait - auto, DefaultKeepRecentTokens);
    }

    /// <summary>Whether the thresholds leave a window of <paramref name="contextWindow"/> tokens at least
    /// <see cref="MinimumUsableTokens"/> before the request blocks. An unknown window (0) always fits.</summary>
    public bool Fits(double contextWindow) => !(contextWindow > 0) || contextWindow - ReserveTokens >= MinimumUsableTokens;

    /// <summary>Where the next request waits for a summary.</summary>
    public double WaitAt(double contextWindow) => contextWindow - ReserveTokens;

    /// <summary>Where a background summary starts; null when background compaction is off.</summary>
    public double? AutoAt(double contextWindow) => BackgroundTokens == 0 ? null : WaitAt(contextWindow) - BackgroundTokens;

    /// <summary>Throws <see cref="HostInvalidException"/> for a value out of durable-pi's ranges: <see cref="ReserveTokens"/> 1024 to
    /// 1000000, <see cref="BackgroundTokens"/> 0 to 1000000, <see cref="KeepRecentTokens"/> 500 to 200000, all whole numbers.</summary>
    public SessionCompaction Validate()
    {
        Range(ReserveTokens, 1024, 1_000_000, "reserveTokens");
        Range(BackgroundTokens, 0, 1_000_000, "backgroundTokens");
        Range(KeepRecentTokens, 500, 200_000, "keepRecentTokens");
        return this;
    }

    /// <summary>The Harness settings.</summary>
    public CompactionSettings ToSettings() => new()
    {
        Enabled = Enabled,
        ReserveTokens = ReserveTokens,
        BackgroundTokens = BackgroundTokens,
        KeepRecentTokens = KeepRecentTokens,
    };

    internal string ToJson() => JsonSerializer.Serialize(new Stored(Enabled, ReserveTokens, BackgroundTokens, KeepRecentTokens));

    internal static SessionCompaction? FromJson(string? json)
    {
        if (json is null) return null;
        var stored = JsonSerializer.Deserialize<Stored>(json);
        return stored is null ? null : new SessionCompaction(stored.Enabled, stored.ReserveTokens, stored.BackgroundTokens, stored.KeepRecentTokens);
    }

    private static void Range(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value) || Math.Floor(value) != value || value < minimum || value > maximum)
            throw new HostInvalidException($"{name} must be a whole number from {minimum} to {maximum}.");
    }

    private sealed record Stored(bool Enabled, double ReserveTokens, double BackgroundTokens, double KeepRecentTokens);
}

/// <summary>A partial change of a session's compaction settings; null fields keep their value.</summary>
public sealed record SessionCompactionChange
{
    /// <summary>Automatic compaction on or off; <c>/compact</c> works either way.</summary>
    public bool? Enabled { get; init; }
    /// <summary>See <see cref="SessionCompaction.ReserveTokens"/>.</summary>
    public double? ReserveTokens { get; init; }
    /// <summary>See <see cref="SessionCompaction.BackgroundTokens"/>.</summary>
    public double? BackgroundTokens { get; init; }
    /// <summary>See <see cref="SessionCompaction.KeepRecentTokens"/>.</summary>
    public double? KeepRecentTokens { get; init; }
}

/// <summary>A session's Harness settings: the host's settings with the session's compaction, read at every use. Without a host extension
/// selection, every installed extension but the optional ones is selected.</summary>
internal sealed class SessionHarnessSettings(HarnessSettings? inner, Func<SessionCompaction> compaction, Func<IReadOnlyList<Extension>>? defaultExtensions = null)
    : HarnessSettings
{
    public override IReadOnlyList<Extension>? Extensions => inner?.Extensions ?? defaultExtensions?.Invoke();
    public override ConversationStreamOptions? Stream => inner?.Stream;
    public override RetrySettings? Retry => inner?.Retry;
    public override CompactionSettings? Compaction => compaction().ToSettings();
    public override ProgressSettings? Progress => inner?.Progress;
    public override ToolExecutionMode? ToolExecution => inner?.ToolExecution;
    public override QueueMode? SteeringMode => inner?.SteeringMode;
    public override QueueMode? FollowUpMode => inner?.FollowUpMode;
    public override double? ContextRetentionMs => inner?.ContextRetentionMs;
    public override double? DocumentRetentionMs => inner?.DocumentRetentionMs;
}
