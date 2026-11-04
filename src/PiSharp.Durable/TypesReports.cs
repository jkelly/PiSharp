// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/types.ts (ReportTask, HostCallback, ReportInfo, Report, ReportFrom;
// split from Types.cs), ported in the Pi v1.5.0 sync (area reports: upstream a30c2a81d, 440a5fcdb, 5ba1cbaf3, 91b33cab3 and 5f312dac4's
// report kinds). Upstream's ReportInfo is a union tagged by kind; here it is one record whose Kind says which of its optional fields are
// set, as TaskInspectionState is. A Report is a ReportInfo and the error (upstream's `ReportInfo & { error }`); upstream's `error: unknown`
// is an Exception, as HarnessOptions.OnReport took before. One part of v1.5.0's union arrives with a later area of the sync: the
// task_interrupted kind with its restart_limit and interrupted reasons and its restart count (area restarts). Pi v1.5.0 sync (area
// telemetry): the telemetry host callback (2ec55d808).
using PiSharp.Durable.Chord;
using PiSharp.Durable.Internal;

namespace PiSharp.Durable;

/// <summary>The task a report is about (upstream <c>ReportTask</c>).</summary>
/// <param name="Id">The task.</param>
/// <param name="Kind">The task's registered name.</param>
/// <param name="ConversationId">The task's conversation.</param>
public sealed record ReportTask(TaskId Id, string Kind, ConversationId ConversationId)
{
    /// <summary><c>{id, kind, conversationId}</c>.</summary>
    public JsonObjectValue ToJson() =>
        new RecordJson.Writer().Add("id", Id.ToJson()).Add("kind", Kind).Add("conversationId", ConversationId.ToJson()).Build();
}

/// <summary>
/// A host callback, by its <see cref="HarnessOptions"/> name (upstream <c>HostCallback</c>): <see cref="Env"/> is the environment builder and
/// <see cref="Models"/> a provider call.
/// </summary>
public enum HostCallback
{
    /// <summary><c>now</c>: <see cref="HarnessOptions.Now"/>.</summary>
    Now,
    /// <summary><c>settings</c>: <see cref="HarnessOptions.Settings"/> or <see cref="HarnessOptions.CurrentSettings"/>.</summary>
    Settings,
    /// <summary><c>registry</c>: <see cref="HarnessOptions.Registry"/>.</summary>
    Registry,
    /// <summary><c>env</c>: <see cref="HarnessOptions.Env"/>.</summary>
    Env,
    /// <summary><c>models</c>: a call of <see cref="HarnessOptions.Models"/>.</summary>
    Models,
    /// <summary><c>setTimer</c>: <see cref="HarnessOptions.SetTimer"/>.</summary>
    SetTimer,
    /// <summary><c>onWake</c>: <see cref="HarnessOptions.OnWake"/>.</summary>
    OnWake,
    /// <summary><c>telemetry</c>: <see cref="HarnessOptions.Telemetry"/>, or a context's span, threw while a commit was published (its
    /// <c>pi.durable.entry</c> or <c>pi.durable.submission</c> event, or <c>pi.durable.tasks.active</c>). The commit stands.</summary>
    Telemetry,
}

/// <summary>What a report to <see cref="HarnessOptions.OnReport"/> is about (upstream <c>ReportInfo["kind"]</c>).</summary>
public enum ReportKind
{
    /// <summary><c>storage_failed</c>: the error failed the Session, which closes: a Storage error, often a platform reset.</summary>
    StorageFailed,
    /// <summary><c>task_faulted</c>: the task ended <c>faulted</c>; its phase threw (<see cref="ReportReason.Threw"/>).</summary>
    TaskFaulted,
    /// <summary><c>migration_failed</c>: the task's stored state could not be migrated; it is blocked until the registry changes.</summary>
    MigrationFailed,
    /// <summary>
    /// <c>task_outdated</c>: a running task keeps running under its old definition, which was uninstalled (<see cref="ReportReason.MissingTask"/>)
    /// or replaced by one that cannot take over its state: older than the task (<see cref="ReportReason.TaskTooOld"/>), or newer without a
    /// migration (<see cref="ReportReason.MigrationFailed"/>).
    /// </summary>
    TaskOutdated,
    /// <summary><c>task_error</c>: the task's own <see cref="ITaskRuntime.Report"/>, or a failure in running it.</summary>
    TaskError,
    /// <summary><c>extension_error</c>: a hook, a section, a wrapper, or a tool's invalid structured output; with the task that ran it, or,
    /// outside a task, the conversation whose agent was resolved.</summary>
    ExtensionError,
    /// <summary><c>host_error</c>: a host callback threw (<see cref="ReportInfo.Callback"/>); with the task that called it, if one did.</summary>
    HostError,
    /// <summary><c>listener_error</c>: a commit, close, watch, view, or document listener threw.</summary>
    ListenerError,
}

/// <summary>Why a task faulted or is outdated (upstream <c>ReportInfo["reason"]</c>).</summary>
public enum ReportReason
{
    /// <summary><c>threw</c>: a <see cref="ReportKind.TaskFaulted"/> task's phase threw.</summary>
    Threw,
    /// <summary><c>missing_task</c>: no definition has the outdated task's kind.</summary>
    MissingTask,
    /// <summary><c>task_too_old</c>: the replacement definition is older than the task.</summary>
    TaskTooOld,
    /// <summary><c>migration_failed</c>: the replacement definition is newer and has no migration.</summary>
    MigrationFailed,
}

/// <summary>
/// What a report to <see cref="HarnessOptions.OnReport"/> is about, by <see cref="Kind"/> (upstream <c>ReportInfo</c>). Which fields are set
/// depends on the kind: <see cref="Task"/> for the task kinds, and for an extension or host error a task ran; <see cref="Extension"/>, where
/// present, names the extension whose code failed, when known (for a task's own code, the one that installed the definition the task ran);
/// <see cref="ConversationId"/> for an extension error outside a task; <see cref="Callback"/> for a host error; <see cref="Reason"/> for a
/// faulted or outdated task. Equality is record equality, as upstream's tests compare infos with <c>toEqual</c>.
/// </summary>
/// <param name="Kind">What happened.</param>
public sealed record ReportInfo(ReportKind Kind)
{
    /// <summary><c>{kind: "storage_failed"}</c>.</summary>
    public static ReportInfo StorageFailed { get; } = new(ReportKind.StorageFailed);

    /// <summary><c>{kind: "listener_error"}</c>, the default of a report the Session makes.</summary>
    public static ReportInfo ListenerError { get; } = new(ReportKind.ListenerError);

    /// <summary>The task the report is about, or that ran the failing code.</summary>
    public ReportTask? Task { get; init; }

    /// <summary>The extension whose code failed, when known.</summary>
    public string? Extension { get; init; }

    /// <summary>For an extension error outside a task: the conversation whose agent was resolved.</summary>
    public ConversationId? ConversationId { get; init; }

    /// <summary>For <see cref="ReportKind.HostError"/>: the host callback that threw.</summary>
    public HostCallback? Callback { get; init; }

    /// <summary>For <see cref="ReportKind.TaskFaulted"/> and <see cref="ReportKind.TaskOutdated"/>: why.</summary>
    public ReportReason? Reason { get; init; }

    /// <summary><c>{kind: "host_error", callback}</c>, with the task that called it, if one did.</summary>
    public static ReportInfo HostError(HostCallback callback, ReportTask? task = null) => new(ReportKind.HostError) { Callback = callback, Task = task };

    /// <summary>Upstream's spelling of <paramref name="kind"/> (<c>storage_failed</c>, <c>task_faulted</c>, ...).</summary>
    public static string KindName(ReportKind kind) => kind switch
    {
        ReportKind.StorageFailed => "storage_failed",
        ReportKind.TaskFaulted => "task_faulted",
        ReportKind.MigrationFailed => "migration_failed",
        ReportKind.TaskOutdated => "task_outdated",
        ReportKind.TaskError => "task_error",
        ReportKind.ExtensionError => "extension_error",
        ReportKind.HostError => "host_error",
        ReportKind.ListenerError => "listener_error",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Invalid ReportKind"),
    };

    /// <summary>Upstream's spelling of <paramref name="reason"/> (<c>threw</c>, <c>missing_task</c>, <c>task_too_old</c>,
    /// <c>migration_failed</c>).</summary>
    public static string ReasonName(ReportReason reason) => reason switch
    {
        ReportReason.Threw => "threw",
        ReportReason.MissingTask => "missing_task",
        ReportReason.TaskTooOld => "task_too_old",
        ReportReason.MigrationFailed => "migration_failed",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Invalid ReportReason"),
    };

    /// <summary>Upstream's spelling of <paramref name="callback"/>: its <see cref="HarnessOptions"/> name (<c>now</c>, <c>setTimer</c>, ...).</summary>
    public static string CallbackName(HostCallback callback) => WireName.Of(callback);

    /// <summary>The info as upstream's JSON object, without the error: <c>{kind, task?, extension?, conversationId?, callback?, reason?}</c>.</summary>
    public JsonObjectValue ToJson() => new RecordJson.Writer()
        .Add("kind", KindName(Kind))
        .Add("task", Task?.ToJson())
        .Add("extension", Extension)
        .Add("conversationId", ConversationId?.ToJson())
        .Add("callback", Callback is { } callback ? CallbackName(callback) : null)
        .Add("reason", Reason is { } reason ? ReasonName(reason) : null)
        .Build();

    /// <inheritdoc/>
    public override string ToString() => ToJson().ToJson();
}

/// <summary>
/// One report to <see cref="HarnessOptions.OnReport"/>: what it is about (<see cref="Info"/>, by kind) and the error reported (upstream
/// <c>Report</c>, <c>ReportInfo &amp; { error }</c>). The info's fields are also readable here, as upstream's report spreads them.
/// </summary>
/// <param name="Info">What the report is about.</param>
/// <param name="Error">The error reported.</param>
public sealed record Report(ReportInfo Info, Exception Error)
{
    /// <summary><see cref="ReportInfo.Kind"/>.</summary>
    public ReportKind Kind => Info.Kind;
    /// <summary><see cref="ReportInfo.Task"/>.</summary>
    public ReportTask? Task => Info.Task;
    /// <summary><see cref="ReportInfo.Extension"/>.</summary>
    public string? Extension => Info.Extension;
    /// <summary><see cref="ReportInfo.ConversationId"/>.</summary>
    public ConversationId? ConversationId => Info.ConversationId;
    /// <summary><see cref="ReportInfo.Callback"/>.</summary>
    public HostCallback? Callback => Info.Callback;
    /// <summary><see cref="ReportInfo.Reason"/>.</summary>
    public ReportReason? Reason => Info.Reason;
}

/// <summary>Whose code a task's <see cref="ITaskRuntime.Report"/> is about (upstream <c>ReportFrom["kind"]</c>).</summary>
public enum ReportFromKind
{
    /// <summary><c>extension</c>: an extension's code the task called, such as a section.</summary>
    Extension,
    /// <summary><c>host</c>: a host callback the task called, such as the environment builder.</summary>
    Host,
}

/// <summary>
/// Whose code a task's <see cref="ITaskRuntime.Report"/> is about when not the task's own, tagged by <see cref="Kind"/> (upstream
/// <c>ReportFrom</c>): <c>{kind: "extension", extension?}</c> (<see cref="FromExtension"/>) or <c>{kind: "host", callback}</c>
/// (<see cref="Host"/>).
/// </summary>
public sealed record ReportFrom
{
    private ReportFrom(ReportFromKind kind, string? extension, HostCallback? callback)
    {
        Kind = kind;
        Extension = extension;
        Callback = callback;
    }

    /// <summary>Whose code failed.</summary>
    public ReportFromKind Kind { get; }

    /// <summary>For <see cref="ReportFromKind.Extension"/>: the extension, when known.</summary>
    public string? Extension { get; }

    /// <summary>For <see cref="ReportFromKind.Host"/>: the host callback.</summary>
    public HostCallback? Callback { get; }

    /// <summary><c>{kind: "extension", extension?}</c>: the report is an <see cref="ReportKind.ExtensionError"/>.</summary>
    public static ReportFrom FromExtension(string? extension = null) => new(ReportFromKind.Extension, extension, null);

    /// <summary><c>{kind: "host", callback}</c>: the report is a <see cref="ReportKind.HostError"/>.</summary>
    public static ReportFrom Host(HostCallback callback) => new(ReportFromKind.Host, null, callback);
}
