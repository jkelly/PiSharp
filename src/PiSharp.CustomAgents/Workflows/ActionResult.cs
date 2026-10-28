// Decision 0012: what an action returns, and the per-action options (next action, retries, timeout, visit limit).
namespace PiSharp.CustomAgents.Workflows;

/// <summary>What happens after an action: continue, jump, stop, fail, or wait first.</summary>
public enum ActionResultKind
{
    /// <summary>Continue with the action's next action (its <see cref="ActionOptions.Next"/>, else the following one).</summary>
    Next,
    /// <summary>Continue with a named action, or a router.</summary>
    GoTo,
    /// <summary>Complete the workflow successfully.</summary>
    Stop,
    /// <summary>Fail the workflow; no retry.</summary>
    Fail,
    /// <summary>Run the same action again after a delay (polling).</summary>
    Repeat,
    /// <summary>Wait, then continue with a named action or the next one.</summary>
    Sleep,
}

/// <summary>The outcome of one action (<see cref="IAction{TData}.ExecuteAsync"/>). The workflow commits the action's Data changes and
/// this decision atomically.</summary>
public sealed record ActionResult
{
    private ActionResult(ActionResultKind kind, string? target = null, string? reason = null, TimeSpan? delay = null)
    {
        Kind = kind;
        Target = target;
        Reason = reason;
        Delay = delay;
    }

    /// <summary>The decision.</summary>
    public ActionResultKind Kind { get; }
    /// <summary>The action to continue with, for <see cref="ActionResultKind.GoTo"/> and <see cref="ActionResultKind.Sleep"/>.</summary>
    public string? Target { get; }
    /// <summary>Why the workflow failed, for <see cref="ActionResultKind.Fail"/>.</summary>
    public string? Reason { get; }
    /// <summary>How long to wait, for <see cref="ActionResultKind.Repeat"/> and <see cref="ActionResultKind.Sleep"/>.</summary>
    public TimeSpan? Delay { get; }
    /// <summary>Data that replaces the action's Data instance, as JSON (a JavaScript action returns merge-patched Data this way); the
    /// engine reads it with the workflow's serializer before committing.</summary>
    internal PiSharp.Durable.Chord.JsonValue? ReplacedData { get; init; }

    private static readonly ActionResult next = new(ActionResultKind.Next);
    private static readonly ActionResult stop = new(ActionResultKind.Stop);

    /// <summary>Continue with the next action.</summary>
    public static ActionResult Next() => next;
    /// <summary>Continue with <paramref name="action"/> (an action or router name, or <see cref="Workflow.End"/>).</summary>
    public static ActionResult GoTo(string action) => new(ActionResultKind.GoTo, Require(action, nameof(action)));
    /// <summary>Complete the workflow.</summary>
    public static ActionResult Stop() => stop;
    /// <summary>Fail the workflow with <paramref name="reason"/>.</summary>
    public static ActionResult Fail(string reason) => new(ActionResultKind.Fail, reason: Require(reason, nameof(reason)));
    /// <summary>Run this action again after <paramref name="after"/>, as a new visit.</summary>
    public static ActionResult Repeat(TimeSpan after) => new(ActionResultKind.Repeat, delay: NonNegative(after));
    /// <summary>Wait <paramref name="delay"/> durably, then continue with <paramref name="then"/>, or the next action when null.</summary>
    public static ActionResult Sleep(TimeSpan delay, string? then = null) => new(ActionResultKind.Sleep, then, delay: NonNegative(delay));

    /// <inheritdoc/>
    public override string ToString() => Kind switch
    {
        ActionResultKind.GoTo => "GoTo(" + Target + ")",
        ActionResultKind.Fail => "Fail(" + Reason + ")",
        ActionResultKind.Repeat => "Repeat(" + Delay + ")",
        ActionResultKind.Sleep => "Sleep(" + Delay + (Target is null ? "" : ", " + Target) + ")",
        _ => Kind.ToString(),
    };

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A value is required.", name) : value;

    private static TimeSpan NonNegative(TimeSpan value) =>
        value < TimeSpan.Zero ? throw new ArgumentOutOfRangeException(nameof(value), "The delay cannot be negative.") : value;
}

/// <summary>How a failed action is retried. Each retry is a new attempt of the same visit: the Data changes of the failed attempt are
/// discarded, and <see cref="ActionContext.OnceAsync{T}"/> results are kept.</summary>
public sealed record RetryPolicy
{
    /// <summary>No retries (the default).</summary>
    public static RetryPolicy None { get; } = new() { MaxAttempts = 1 };

    /// <summary>Up to <paramref name="attempts"/> attempts, waiting <paramref name="delay"/> (default 1 s) doubled after each failure.</summary>
    public static RetryPolicy Exponential(int attempts, TimeSpan? delay = null, TimeSpan? maxDelay = null) =>
        new() { MaxAttempts = attempts, Delay = delay ?? TimeSpan.FromSeconds(1), Backoff = 2, MaxDelay = maxDelay ?? TimeSpan.FromMinutes(5) };

    /// <summary>Up to <paramref name="attempts"/> attempts, waiting <paramref name="delay"/> (default none) between them.</summary>
    public static RetryPolicy Fixed(int attempts, TimeSpan? delay = null) => new() { MaxAttempts = attempts, Delay = delay ?? TimeSpan.Zero };

    /// <summary>Total attempts, the first included; at least 1.</summary>
    public int MaxAttempts { get; init; } = 1;
    /// <summary>The wait before the second attempt.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.Zero;
    /// <summary>The factor applied to the wait after each further failure; 1 keeps it fixed.</summary>
    public double Backoff { get; init; } = 1;
    /// <summary>The longest wait.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Which exceptions are retried; null retries every exception except cancellation.</summary>
    public Func<Exception, bool>? RetryOn { get; init; }

    /// <summary>The wait before attempt <paramref name="attempt"/> + 1, after attempt <paramref name="attempt"/> failed.</summary>
    internal TimeSpan DelayAfter(int attempt)
    {
        var milliseconds = Delay.TotalMilliseconds * Math.Pow(Math.Max(1, Backoff), Math.Max(0, attempt - 1));
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaxDelay.TotalMilliseconds));
    }
}

/// <summary>Options for one action.</summary>
public sealed record ActionOptions
{
    /// <summary>The action that <see cref="ActionResult.Next"/> continues with; default the following action, or the end.</summary>
    public string? Next { get; init; }
    /// <summary>How exceptions are retried; default <see cref="RetryPolicy.None"/>.</summary>
    public RetryPolicy? Retry { get; init; }
    /// <summary>The time one attempt may take before it is cancelled and counts as failed (then retried by <see cref="Retry"/>).</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary>How many times the action may be entered in one run; default unlimited (the workflow's step limit still applies).</summary>
    public int? MaxVisits { get; init; }
}

/// <summary>Limits of one workflow run.</summary>
public sealed record WorkflowLimits
{
    /// <summary>The default limits.</summary>
    public static WorkflowLimits Default { get; } = new();
    /// <summary>The most actions one run may enter (loops included); default 1000.</summary>
    public int MaxSteps { get; init; } = 1000;
    /// <summary>The largest serialized Data, in UTF-8 bytes; default 256 KB.</summary>
    public int MaxDataBytes { get; init; } = 256 * 1024;
}
