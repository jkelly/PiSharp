using System.Text.Json.Nodes;
using PiSharp.Chord.Delta;
using PiSharp.Chord.Documents;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/documents/residency.test.ts. Upstream's fake timers are a
// FakeTimers TimeProvider the residency takes (FakeTimers.cs), one per case as fakeTimers() makes them per test; upstream's
// `{ evict(resident) }` slot is an IResidentSlot.
internal static partial class Program
{
    /// <summary>A resident's slot that records the keys it evicted.</summary>
    private sealed class ResidencyEvicted : IResidentSlot
    {
        public List<string> Keys { get; } = [];

        public void Evict(Resident resident) => Keys.Add(resident.Key);
    }

    /// <summary>A residency whose sweeps run at once (no line), and the keys it evicted, in order (upstream <c>idle</c>).</summary>
    private static (Residency Residency, Func<string, Resident> Resident, List<string> Evicted, FakeTimers Timers) ResidencyIdle()
    {
        var timers = new FakeTimers();
        var slot = new ResidencyEvicted();
        var residency = new Residency(sweep => sweep(), timers);
        return (residency, key => new Resident(slot, key, Tracker.Track(new JsonObject()), null, 1), slot.Keys, timers);
    }

    private static IEnumerable<(string, Func<Task>)> DocumentsResidencyCases() =>
    [
        Case("documents.residency.the-idle-structure-evict-idle-documents-oldest-first-once-their-retention-passed-with-one-timer", async () =>
        {
            var (residency, resident, evicted, timers) = ResidencyIdle();
            var a = resident("a");
            var b = resident("b");
            residency.Touch(a);
            timers.AdvanceTimersByTime(5_000);
            residency.Touch(b);
            Equal(1, timers.TimerCount, "one timer for two idle documents");
            timers.AdvanceTimersByTime(Residency.RetentionMs - 5_000);
            await FakeTimers.Flushed();
            Sequence(["a"], evicted, "evicted at a's retention");
            Equal(1, timers.TimerCount, "the timer for b");
            timers.AdvanceTimersByTime(5_000);
            Sequence(["a", "b"], evicted, "evicted at b's retention");
            Equal(0, timers.TimerCount, "no timer left");
            timers.AdvanceTimersByTime(Residency.RetentionMs);
            Sequence(["a", "b"], evicted, "evicted after");
        }),
        Case("documents.residency.the-idle-structure-move-a-touched-document-to-the-back-at-most-once-per-granule", () =>
        {
            var (residency, resident, evicted, timers) = ResidencyIdle();
            var a = resident("a");
            var b = resident("b");
            residency.Touch(a);
            residency.Touch(b);
            timers.AdvanceTimersByTime(500);
            residency.Touch(a);
            timers.AdvanceTimersByTime(Residency.RetentionMs - 500);
            // Within its granule the touch did not move `a`: it leaves up to a granule early.
            Sequence(["a", "b"], evicted, "a touch within the granule");
            residency.Touch(a);
            residency.Touch(b);
            timers.AdvanceTimersByTime(1_000);
            residency.Touch(a);
            timers.AdvanceTimersByTime(Residency.RetentionMs - 1_000);
            Sequence(["a", "b", "b"], evicted, "a touch a granule later");
            timers.AdvanceTimersByTime(1_000);
            Sequence(["a", "b", "b", "a"], evicted, "a moved to the back");
        }),
        Case("documents.residency.the-idle-structure-forget-pinned-documents-after-end-nothing-is-evicted-and-no-timer-is-left", () =>
        {
            var (residency, resident, evicted, timers) = ResidencyIdle();
            var a = resident("a");
            residency.Touch(a);
            residency.Forget(a);
            timers.AdvanceTimersByTime(Residency.RetentionMs);
            Sequence([], evicted, "a forgotten document");
            residency.Touch(resident("b"));
            residency.End();
            Equal(0, timers.TimerCount, "no timer after end");
            timers.AdvanceTimersByTime(Residency.RetentionMs);
            Sequence([], evicted, "nothing evicted after end");
        }),
    ];
}
