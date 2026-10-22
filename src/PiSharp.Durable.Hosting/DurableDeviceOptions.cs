// PiSharp-only (decision 0008). What a host passes in instead of the CLI pieces a library cannot reference: models (the CLI's DurableModels,
// NativeDurableModels or FauxDurableModels), base Harness settings (the CLI's PiHarnessSettings) and a registry per session. SessionStream
// (decision 0011) lets a host bind each session's model requests to its own routing data, such as the web agent's chat accounts.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>How a <see cref="DurableDeviceHost"/> runs.</summary>
public sealed record DurableDeviceOptions
{
    /// <summary>Where the catalog, the sessions and the archive live. Created when missing; <c>~</c> is the home directory.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Where new sessions start; at least one. Created when missing. They choose where sessions begin and are not a sandbox: an
    /// agent can reach anything the host's user can.</summary>
    public required IReadOnlyList<string> StartDirectories { get; init; }

    /// <summary>The device's models; provider keys stay with them on the device.</summary>
    public required IDurableModels Models { get; init; }

    /// <summary>The device's name; null is the machine name.</summary>
    public string? DeviceName { get; init; }

    /// <summary>The model of a session created without one, and of conversations whose stored model the device does not know (they are
    /// re-routed to it when their session opens). Null: sessions must name a model, and nothing is re-routed.</summary>
    public ModelRef? DefaultModel { get; init; }

    /// <summary>Main's thinking level when a session does not choose one.</summary>
    public string DefaultThinkingLevel { get; init; } = "low";

    /// <summary>Harness settings shared by every session, read at every use; each session replaces only the compaction settings with its
    /// own, and adds its <see cref="SessionStream"/> metadata. Null: pi-durable's defaults.</summary>
    public HarnessSettings? Settings { get; init; }

    /// <summary>
    /// The stream metadata of a session's model requests: given the session ID, the fields to send under <c>metadata.pisharp</c> with every
    /// request the session prepares (the web agent sends the chat's <c>account</c> and <c>tier</c>), or null for none. The host adds
    /// <c>op</c>, a new operation ID for each prepared request (see <see cref="SessionStreamMetadata"/>).
    /// <para>The value is read when a generation or summary request is prepared and saved with it in the task's checkpoint, so the request
    /// keeps its fields and <c>op</c> when it is recovered after a restart, even if the hook answers differently by then. A host changes
    /// a session's future binding by answering differently: requests prepared later use the new value. A retry prepares again and gets a
    /// new <c>op</c>.</para>
    /// <para>Called synchronously whenever a session's Harness resolves its settings, which is more often than requests are prepared:
    /// keep it cheap and non-blocking. A throw is reported through <see cref="OnReport"/>, and that request carries <c>op</c> alone.
    /// <see cref="IDurableModels"/> receives the object in <see cref="DurableStreamOptions.Metadata"/>; providers must never see it
    /// (NativeDurableModels' own routes send only <c>metadata.user_id</c>, and a host route strips it with
    /// <see cref="SessionStreamMetadata.Strip"/>). Null: requests carry no <c>pisharp</c> metadata.</para>
    /// </summary>
    public Func<string, JsonObjectValue?>? SessionStream { get; init; }

    /// <summary>The registry of one session. Null: the durable coding tools (<c>read</c>, <c>write</c>, <c>edit</c>, <c>bash</c>) and
    /// <c>user-interaction</c> (<c>ask_user_question</c>, <c>exit_plan_mode</c>).</summary>
    public Func<HostedSessionContext, IRegistry>? CreateRegistry { get; init; }

    /// <summary>Opens a session's storage at a database path. Null: SQLite (tests wrap it to fail on demand).</summary>
    public Func<string, Task<IStorage>>? OpenStorage { get; init; }

    /// <summary>Which tools the read-only switch removes.</summary>
    public ToolEffects ToolEffects { get; init; } = ToolEffects.Default;

    /// <summary>The clock of creation times and the Harness.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Errors that do not fail a call: a session that failed to open or later, a Harness report, a change listener that threw.
    /// The first argument is the session ID, when there is one.</summary>
    public Action<string?, Exception>? OnReport { get; init; }

    /// <summary>How many event batches an <see cref="AgentEventFeed"/> holds before its watch waits.</summary>
    public int FeedCapacity { get; init; } = 64;

    /// <summary>The durable jobs agents and people can run on this device.</summary>
    public IReadOnlyList<JobDefinition> Jobs { get; init; } = SampleJobs.All;

    /// <summary>Run persisted schedules in this host. False lets an embedding host supply ticks itself.</summary>
    public bool RunSchedules { get; init; } = true;

    /// <summary>How often the host checks for due schedules; scheduling uses <see cref="TimeProvider"/>.</summary>
    public TimeSpan SchedulePollInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Where the <c>system-prompts</c> extension finds its prompt files (<c>*.md</c>); null is <c>prompts</c> under the data
    /// directory.</summary>
    public string? PromptsDirectory { get; init; }

    /// <summary>The prompt files an agent has checked until it changes its checklist.</summary>
    public IReadOnlyList<string> DefaultPrompts { get; init; } = [];

    /// <summary>The <c>command-guard</c> extension's judge for commands its offline rules allow, for example a model classifier; null uses
    /// the offline rules only. A null verdict allows the command.</summary>
    public Func<string, CancellationToken, Task<CommandVerdict?>>? CommandJudge { get; init; }

    /// <summary>Whether a command runs when <see cref="CommandJudge"/> fails (durable-pi's guard fails open).</summary>
    public bool CommandGuardFailOpen { get; init; } = true;

    /// <summary>How sources are fetched: hard limits, private hosts that may be reached, credentials and optional fallback providers.</summary>
    public RetrievalOptions Retrieval { get; init; } = new();

    /// <summary>How long the catalog keeps settled application records (see <see cref="DeviceRetention"/>).</summary>
    public RetentionPolicy Retention { get; init; } = new();
}

/// <summary>The session a registry is made for.</summary>
/// <param name="SessionId">The session's UUID.</param>
/// <param name="Cwd">Its working directory.</param>
/// <param name="Directory">Its directory under the data directory.</param>
public sealed record HostedSessionContext(string SessionId, string Cwd, string Directory)
{
    /// <summary>The device's extensions for this session: <c>durable-apps</c> (App State, history, subagent, sessions, handoff and job tools
    /// and the job tasks), then the optional <c>system-prompts</c> and <c>command-guard</c>. A custom registry installs them to offer
    /// them.</summary>
    public IReadOnlyList<Extension> Apps { get; init; } = [];
}
