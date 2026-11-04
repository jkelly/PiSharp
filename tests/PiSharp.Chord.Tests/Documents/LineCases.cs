using PiSharp.Chord.Documents;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/documents/line.test.ts. Upstream's flushed() is
// FakeTimers.Flushed (FakeTimers.cs); the holders' continuations run on the thread pool, so the order list is locked.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> DocumentsLineCases() =>
    [
        Case("documents.line.the-line-hands-the-line-to-each-holder-in-the-order-they-asked-once-the-previous-one-released", async () =>
        {
            var line = new Line();
            var order = new List<string>();
            async Task<Action> Then(Task<Action> acquired, string name)
            {
                var release = await acquired;
                lock (order) order.Add(name);
                return release;
            }
            var first = await line.AcquireAsync();
            var second = Then(line.AcquireAsync(), "second");
            var third = Then(line.AcquireAsync(), "third");
            await FakeTimers.Flushed();
            lock (order) Sequence([], order, "nobody holds the line before the first releases");
            first();
            (await second)();
            await third;
            lock (order) Sequence(["second", "third"], order, "the holders");
        }),
        Case("documents.line.the-line-ignores-a-second-release-of-the-same-holder", async () =>
        {
            var line = new Line();
            var first = await line.AcquireAsync();
            var second = line.AcquireAsync();
            var thirdHeld = false;
            var third = Then(line.AcquireAsync());
            async Task Then(Task<Action> acquired)
            {
                await acquired;
                Volatile.Write(ref thirdHeld, true);
            }
            first();
            first();
            await second;
            await FakeTimers.Flushed();
            Equal(false, Volatile.Read(ref thirdHeld), "the third before the second released");
            (await second)();
            await third;
            Equal(true, Volatile.Read(ref thirdHeld), "the third after the second released");
        }),
    ];
}
