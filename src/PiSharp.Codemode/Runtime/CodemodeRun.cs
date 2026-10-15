// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/codemode/src/types.ts (CodemodeRun) and
// packages/codemode/src/runtime/execution.ts (the object CodemodeSandboxBase.start() returns).
namespace PiSharp.Codemode;

/// <summary>A running <see cref="CodemodeSandbox.Start"/> execution.</summary>
public sealed class CodemodeRun
{
    private readonly CodemodeSandbox.Execution execution;

    internal CodemodeRun(CodemodeSandbox.Execution execution, Task<CodemodeResult> result)
    {
        this.execution = execution;
        Result = result;
    }

    /// <summary>Never faults for script failures; those come back as a failed result.</summary>
    public Task<CodemodeResult> Result { get; }

    /// <summary>Whether the run has its outcome: the script ended, timed out or failed, or the run was aborted. Set when that is decided,
    /// before <see cref="Result"/> completes (which waits for the engine to stop); calls can no longer be answered.</summary>
    public bool Finished => execution.Finished;

    /// <summary>
    /// Answers call <paramref name="id"/> as result number <paramref name="seq"/>. Results are numbered 1, 2, ... without gaps, and the
    /// engine applies them strictly in that order, whatever order they arrive in: after a gap it waits for the missing number. While it
    /// processes this one, its clock reads <paramref name="now"/> (ms since the epoch), or the previous value if <paramref name="now"/>
    /// is earlier. Does nothing once the run is <see cref="Finished"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A <paramref name="now"/> that is not finite, a <paramref name="seq"/> that is not positive or
    /// was used before, or failure properties that are not a JSON object. The call keeps waiting.</exception>
    /// <exception cref="InvalidOperationException">Call <paramref name="id"/> is not waiting for an answer.</exception>
    public void Deliver(long seq, double now, long id, CodemodeOutcome outcome) => execution.Deliver(seq, now, id, outcome);

    /// <summary>Ends the run with an <see cref="CodemodeErrorKind.Aborted"/> error carrying <paramref name="message"/>, which the script
    /// cannot catch: the counterpart of aborting the run's <c>AbortSignal</c> with an <c>Error</c> as its reason. Completes with
    /// <see cref="Result"/>; does nothing to a finished run.</summary>
    public Task<CodemodeResult> AbortAsync(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _ = execution.AbortAsync(message);
        return Result;
    }
}
