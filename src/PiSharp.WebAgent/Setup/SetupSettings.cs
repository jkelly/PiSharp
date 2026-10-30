// Decision 0011 (run W3, SU): the setup section of ISettingsStore (key "setup"), which records whether the first-run wizard was finished
// or skipped, and when. Nothing else is kept here: what the wizard connects is the credentials area's (accounts, the vault) and the default
// model it saves is the Defaults section's, so the wizard writes nothing the settings pages do not already own. A missing section reads as
// "never finished or skipped"; together with "nothing connected" that is what sends / and /chat/new to /setup (SetupRedirect).
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Setup;

/// <summary>How the first-run wizard ended. Stored and sent as <c>finished</c> or <c>skipped</c>.</summary>
public enum SetupOutcome
{
    /// <summary>The user went through the wizard and saved its last step (Save and continue).</summary>
    Finished,

    /// <summary>The user chose Skip setup.</summary>
    Skipped,
}

/// <summary>Whether the first-run wizard was finished or skipped (the <c>setup</c> section). Holds no secret.</summary>
/// <param name="Outcome">How the wizard ended, or null when it never did.</param>
/// <param name="At">When it ended (UTC), or null.</param>
public sealed record SetupSettings(SetupOutcome? Outcome = null, DateTimeOffset? At = null) : ISettingsSection<SetupSettings>
{
    /// <inheritdoc/>
    public static string SectionName => "setup";

    /// <inheritdoc/>
    public static SetupSettings CreateDefault() => new();

    /// <summary>Whether the wizard was finished or skipped, so the first-run redirect no longer applies.</summary>
    public bool Done => Outcome is not null;

    /// <summary>Whether the wizard was finished (its Done step shows what was set up).</summary>
    public bool Finished => Outcome == SetupOutcome.Finished;

    /// <summary>The section that records <paramref name="outcome"/> at <paramref name="at"/>.</summary>
    public static SetupSettings Ended(SetupOutcome outcome, DateTimeOffset at) => new(outcome, at.ToUniversalTime());

    /// <inheritdoc/>
    public SetupSettings Normalize()
    {
        if (Outcome is not { } outcome) return new();
        if (!Enum.IsDefined(outcome)) throw new ArgumentException($"Unknown setup outcome {(int)outcome}: use finished or skipped.");
        return new(outcome, At?.ToUniversalTime());
    }
}
