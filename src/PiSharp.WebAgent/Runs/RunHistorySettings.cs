// Decision 0011 (run W4, R0): what runs.db keeps and for how long (mockup settings-data, "Run history"; R9 builds that page on this
// section). Runs are kept 30 days by default (null keeps them forever); pinned runs never expire. Raw payloads (the redacted request with its
// headers and body, the response headers, the raw SSE lines and the timing marks) are stored only when the user opts in, and never longer
// than RawRetentionDays (7) whatever the run's own retention. The normalized stream events of each run (the inspector's event list and what
// a reconnecting tab catches up from once a run has finished) are stored unless the user turns them off. The Authorization header, and every
// other header or query parameter that carries a key, is redacted before anything is stored; that is not a setting.
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Runs;

/// <summary>Run history's settings (section <c>history</c>, Settings › Data).</summary>
/// <param name="RetentionDays">Runs older than this many days are deleted at start and every hour after (pinned ones are kept); null keeps
/// them forever. 1 to 3,650; default 30.</param>
/// <param name="StoreRawPayloads">Whether raw payloads are stored (opt-in; default off). They are kept at most
/// <see cref="RawRetentionDays"/> days.</param>
/// <param name="StoreSseEvents">Whether each run's normalized stream events are stored with their arrival times (default on).</param>
public sealed record RunHistorySettings(int? RetentionDays = RunHistorySettings.DefaultRetentionDays, bool StoreRawPayloads = false,
    bool StoreSseEvents = true) : ISettingsSection<RunHistorySettings>
{
    /// <summary>The default retention: 30 days.</summary>
    public const int DefaultRetentionDays = 30;

    /// <summary>The longest retention a page may set: ten years.</summary>
    public const int MaxRetentionDays = 3_650;

    /// <summary>How long raw payloads are kept at most: 7 days.</summary>
    public const int RawRetentionDays = 7;

    /// <inheritdoc/>
    public static string SectionName => "history";

    /// <inheritdoc/>
    public static RunHistorySettings CreateDefault() => new();

    /// <summary>The age past which an unpinned run is deleted, or null when runs are kept forever.</summary>
    public TimeSpan? Retention => RetentionDays is { } days ? TimeSpan.FromDays(days) : null;

    /// <inheritdoc/>
    public RunHistorySettings Normalize()
    {
        if (RetentionDays is { } days && (days < 1 || days > MaxRetentionDays))
            throw new ArgumentException($"Keep runs for 1 to {MaxRetentionDays:N0} days, or forever.");
        return this;
    }
}
