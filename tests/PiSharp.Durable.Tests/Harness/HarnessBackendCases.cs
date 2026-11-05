// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-tasks.test.ts on the file backends. Upstream runs this
// file on MemoryStorage only; the task groups here also run on a fresh JSONL directory and a fresh SQLite file per case, as the session
// groups do (SessionBackendCases.cs).
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage.Jsonl;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessJsonlCases() =>
        HarnessTests.TaskCases("harness.tasks.jsonl.", () => JsonlStorage.OpenAsync(Temp("harness-jsonl"), DurableContext.Background).GetAwaiter().GetResult());

    private static IEnumerable<(string, Func<Task>)> HarnessSqliteCases() =>
        HarnessTests.TaskCases("harness.tasks.sqlite.", () => SqliteStorage.OpenAsync(Path.Combine(Temp("harness-sqlite"), "harness.sqlite")).GetAwaiter().GetResult());
}
