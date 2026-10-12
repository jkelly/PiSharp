using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage.Jsonl;

// Milestone M0 (decision 0005): the session.* groups ported from Pi v1.1.0 packages/durable/test/session-*.test.ts and chord-guide.test.ts
// also run on the file backends, each case on a fresh JSONL directory or SQLite file (upstream runs its storage-agnostic suites on every
// backend the same way).
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SessionJsonlCases() =>
        SessionTests.All("session.jsonl.", () => JsonlStorage.OpenAsync(Temp("session-jsonl"), DurableContext.Background).GetAwaiter().GetResult());

    private static IEnumerable<(string, Func<Task>)> SessionSqliteCases() =>
        SessionTests.All("session.sqlite.", () => SqliteStorage.OpenAsync(Path.Combine(Temp("session-sqlite"), "session.sqlite")).GetAwaiter().GetResult());
}
