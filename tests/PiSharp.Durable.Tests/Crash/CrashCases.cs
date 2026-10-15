// Authored for PiSharp (no upstream test file): the parent half of the real-process crash tests (decision 0005, "Crash tests"); the
// scenarios and the child protocol are in CrashScenarios.cs. Each case crash.<backend>.<scenario>.<checkpoint> launches the test executable
// as a child, waits for its CHECKPOINT sentinel, kills the whole process tree, then reopens the same storage in process, as
// `pisharp durable --continue` would (open normally, no cleanup), and asserts recovery. Backends: SQLite and JSONL; MemoryStorage cannot
// survive a process kill, so it has no crash cases.
//
// Spec sections (upstream packages/durable/docs/spec.md) each scenario covers:
// - chat: §1 invariants 1-3 (atomic commit, commit before show, all visible progress durable); §8.3 generation recovery in each phase:
//   prepare reruns, request resends the pinned request, a committed partial becomes an aborted entry, retry resumes its backoff, a
//   committed answer is final. The turn finishes with exactly one answer and one user entry.
// - tool-unsafe / tool-safe: §5.2 effect sandwich; §8.4 tool recovery: before intent the call phase reruns, after intent a non-replay-safe
//   call is never rerun and settles with the interrupted result, a replay-safe call reruns exactly once; §8.5 the tools phase reruns
//   before its commit and resumes after a result commit. The run continues to a final answer.
// - followup: §6 inbox and final boundary: a queued follow-up survives the kill and both inputs settle exactly once.
// - submit: §6 requestId deduplication: a retry with the same request id after a kill admits nothing twice.
// - abort: §5.4 abort protocol and cascade "derived again at open", §5.5 "abort flows down" and owned work finishing first, §8.3 and
//   §8.4 abort handlers: a durable abort mark completes bottom-up on reopen.
// - `*-failed` (Pi v1.2.0 storage failures, README "Errors" rule 1): a batch that fails in Storage fails the Session, which closes itself
//   and retries nothing; the commit is atomic, so reopening recovers exactly as from a kill before that batch: chat.answer-failed as
//   chat.answer-before, tool-unsafe.intent-failed as tool-unsafe.before-tool, tool-unsafe.result-failed as tool-unsafe.execute (a
//   non-replay-safe call that ran and lost its result is interrupted), followup.first-answer-failed as followup.first-request, and
//   submit.admit-failed as submit.admit-before.
using System.Diagnostics;
using System.Reflection;
using System.Text;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using static ChatTests;
using static CrashScenarios;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> CrashCases() =>
        from backend in new[] { "sqlite", "jsonl" }
        from point in CrashTests.Points
        select Case($"crash.{backend}.{point.Scenario}.{point.Checkpoint}", () => CrashTests.RunAsync(backend, point));
}

/// <summary>The crash.* cases.</summary>
internal static class CrashTests
{
    private static readonly TimeSpan SentinelTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(60);

    /// <summary>One crash point: where the child is killed and what holds after reopen.</summary>
    internal sealed record Point(string Scenario, string Checkpoint, Func<Recovery, Task> Verify);

    /// <summary>The reopened Harness of a case and what the case reads from it.</summary>
    internal sealed class Recovery(string backend, string path, ChatSetup setup, ChatTests.OpenedChat opened)
    {
        public string Backend { get; } = backend;
        public string Path { get; } = path;
        public string WorkDir => System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
        public ChatSetup Setup { get; } = setup;
        public IHarness Harness { get; } = opened.Harness;
        public IConversation Root { get; } = opened.Root;
        public int FauxCalls => Setup.Faux.State.CallCount;
        public int ToolRuns => CrashScenarios.ToolRuns(WorkDir);

        public Task<SubmissionRecord?> ByRequest(string requestId) =>
            Root.CommitAsync(tx => tx.GetSubmissionByRequestAsync(Root.Id, requestId), Ctx);

        public async Task Idle() => await Harness.WaitForIdleAsync(Ctx).WaitAsync(RecoveryTimeout);
    }

    internal static readonly Point[] Points =
    [
        // Generation phases of a chat turn: prepare, request, a streaming partial, retry, and either side of the answer commit.
        new("chat", "prepare", r => ChatTurn(r, fauxCalls: 1)),
        new("chat", "request", r => ChatTurn(r, fauxCalls: 1)),
        new("chat", "stream", r => ChatTurn(r, fauxCalls: null)),
        new("chat", "retry", r => ChatTurn(r, fauxCalls: 1, errors: 1)),
        new("chat", "answer-before", r => ChatTurn(r, fauxCalls: 1)),
        new("chat", "answer-after", r => ChatTurn(r, fauxCalls: 0)),
        // The answer commit fails in Storage: the Session fails, and reopening answers as after a kill before it.
        new("chat", "answer-failed", r => ChatTurn(r, fauxCalls: 1)),
        // A non-replay-safe tool: before intent, after the intent commit, mid-execute, after the result commit, in the tools phase.
        new("tool-unsafe", "before-tool", r => ToolTurn(r, runs: 1, interrupted: false, fauxCalls: 1)),
        new("tool-unsafe", "intent-after", r => ToolTurn(r, runs: 0, interrupted: true, fauxCalls: 1)),
        new("tool-unsafe", "execute", r => ToolTurn(r, runs: 1, interrupted: true, fauxCalls: 1)),
        new("tool-unsafe", "result-after", r => ToolTurn(r, runs: 1, interrupted: false, fauxCalls: 1)),
        new("tool-unsafe", "after-tools", r => ToolTurn(r, runs: 1, interrupted: false, fauxCalls: 1)),
        // The intent commit, or the result commit after the call ran, fails in Storage.
        new("tool-unsafe", "intent-failed", r => ToolTurn(r, runs: 1, interrupted: false, fauxCalls: 1)),
        new("tool-unsafe", "result-failed", r => ToolTurn(r, runs: 1, interrupted: true, fauxCalls: 1)),
        // A replay-safe tool reruns exactly once.
        new("tool-safe", "intent-after", r => ToolTurn(r, runs: 1, interrupted: false, fauxCalls: 1)),
        new("tool-safe", "execute", r => ToolTurn(r, runs: 2, interrupted: false, fauxCalls: 1)),
        // A follow-up queued while the first input runs.
        new("followup", "first-request", r => TwoTurns(r, fauxCalls: 2)),
        new("followup", "first-answer-after", r => TwoTurns(r, fauxCalls: 1)),
        new("followup", "second-request", r => TwoTurns(r, fauxCalls: 1)),
        new("followup", "first-answer-failed", r => TwoTurns(r, fauxCalls: 2)),
        // Submission retries with the same request id.
        new("submit", "admit-before", r => Resubmit(r, admitted: false)),
        new("submit", "admit-after", r => Resubmit(r, admitted: true)),
        new("submit", "admit-failed", r => Resubmit(r, admitted: false)),
        new("submit", "queued-admit-after", ResubmitQueued),
        // Abort marks.
        new("abort", "task-mark", r => AbortedToolRound(r)),
        new("abort", "conversation-mark", r => AbortedToolRound(r)),
        new("abort", "request-mark", AbortedRequest),
    ];

    // ─── Case driver ────────────────────────────────────────────────────────

    internal static async Task RunAsync(string backend, Point point)
    {
        var directory = Temp($"crash-{backend}-{point.Scenario}");
        var path = Path.Combine(directory, backend == "sqlite" ? "session.sqlite" : "session-jsonl");
        await RunChildAsync(backend, path, point);
        Observe(backend, path, point, "after kill");

        var setup = BuildSetup(point.Scenario, point.Checkpoint, CrashGate.Parent, directory);
        // The retry backoff of the killed child is 60 s; the reopened host's clock is past it.
        if (point.Checkpoint == "retry") setup.Now = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 120_000;
        var opened = await OpenChat(await OpenStorage(backend, path), setup);
        try
        {
            var recovery = new Recovery(backend, path, setup, opened);
            await point.Verify(recovery);
            var inspection = await opened.Harness.InspectAsync(Ctx);
            Equal(0, inspection.Tasks.Length, "live tasks after recovery");
            Equal(0, inspection.Submissions.Length, "unsettled submissions after recovery");
            if (setup.Reports.Count != 0) throw new InvalidOperationException("Reports: " + string.Join(" | ", setup.Reports.Snapshot()));
        }
        finally
        {
            await opened.Harness.CloseAsync(Ctx);
        }
        // A second plain reopen after a clean close sees the same settled state.
        var storage = await OpenStorage(backend, path);
        try
        {
            var page = await storage.ScanSubmissionsAsync(new SubmissionQuery(), 100, null, Ctx);
            Check(page.Items.All(item => item.Status is SubmissionStatus.Done or SubmissionStatus.Unanswered), "every submission settled after a second reopen");
        }
        finally
        {
            await storage.CloseAsync(Ctx);
        }
        Observe(backend, path, point, "after recovery");
    }

    /// <summary>Launches the child, waits for its sentinel, kills its process tree and waits until it exited.</summary>
    private static async Task RunChildAsync(string backend, string path, Point point)
    {
        var start = ChildStart(["--crash-child", point.Scenario, backend, path, point.Checkpoint]);
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var sentinel = "CHECKPOINT " + point.Checkpoint;
        var outcome = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringBuilder();
        var errors = new StringBuilder();
        process.OutputDataReceived += (_, line) =>
        {
            if (line.Data is null)
            {
                outcome.TrySetResult("EOF");
                return;
            }
            lock (output) output.AppendLine(line.Data);
            if (line.Data == sentinel || line.Data == "IDLE" || line.Data.StartsWith("ERROR ", StringComparison.Ordinal)) outcome.TrySetResult(line.Data);
        };
        process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is not null) lock (errors) errors.AppendLine(line.Data);
        };
        string Transcript()
        {
            lock (output) lock (errors) return $"\nchild stdout:\n{output}\nchild stderr:\n{Tail(errors.ToString(), 4000)}";
        }
        try
        {
            if (!process.Start()) throw new InvalidOperationException("The crash child did not start");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            string result;
            try
            {
                result = await outcome.Task.WaitAsync(SentinelTimeout);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"No '{sentinel}' within {SentinelTimeout.TotalSeconds} s" + Transcript());
            }
            if (result != sentinel) throw new InvalidOperationException($"The crash child ended with '{result}' before '{sentinel}'" + Transcript());
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(ExitTimeout);
        }
        finally
        {
            if (!HasExited(process))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                try { await process.WaitForExitAsync().WaitAsync(ExitTimeout); } catch (TimeoutException) { }
            }
        }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static string Tail(string text, int length) => text.Length <= length ? text : "..." + text[^length..];

    /// <summary>
    /// Relaunches this test executable. Under the <c>dotnet</c> muxer (<c>dotnet PiSharp.Durable.Tests.dll</c>) the child is the same host
    /// with the entry assembly; under the apphost (<c>dotnet run</c>) it is the apphost itself. The child inherits the environment; an
    /// apphost child also gets <c>DOTNET_ROOT</c> pointing at the running runtime's root when none is set, so it binds the same runtime on
    /// every platform.
    /// </summary>
    private static ProcessStartInfo ChildStart(IEnumerable<string> arguments)
    {
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("No process path");
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("No entry assembly"));
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT")))
        {
            // <root>/shared/Microsoft.NETCore.App/<version>/
            var runtime = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
            if (runtime.Parent?.Parent?.Parent is { } root && File.Exists(Path.Combine(root.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")))
                start.Environment["DOTNET_ROOT"] = root.FullName;
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>Reports what a hard kill left behind: SQLite WAL and shared-memory files and whether the database can be opened exclusively,
    /// or torn final lines in JSONL files. Observations only; nothing is cleaned up.</summary>
    private static void Observe(string backend, string path, Point point, string when)
    {
        var id = $"crash.{backend}.{point.Scenario}.{point.Checkpoint}";
        if (backend == "sqlite")
        {
            static string Size(string file) => File.Exists(file) ? new FileInfo(file).Length.ToString(System.Globalization.CultureInfo.InvariantCulture) : "absent";
            string exclusive;
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                exclusive = "yes";
            }
            catch (IOException error)
            {
                exclusive = "no (" + error.Message.ReplaceLineEndings(" ") + ")";
            }
            Console.Error.WriteLine($"crash-observe {id} {when}: db={Size(path)} wal={Size(path + "-wal")} shm={Size(path + "-shm")} exclusive-open={exclusive}");
        }
        else
        {
            var files = Directory.Exists(path) ? Directory.GetFiles(path, "*.jsonl") : [];
            var torn = files.Where(file =>
            {
                var bytes = File.ReadAllBytes(file);
                return bytes.Length > 0 && bytes[^1] != (byte)'\n';
            }).Select(Path.GetFileName).ToList();
            Console.Error.WriteLine($"crash-observe {id} {when}: files={files.Length} torn={(torn.Count == 0 ? "none" : string.Join(",", torn))}");
        }
    }

    // ─── Assertions ─────────────────────────────────────────────────────────

    private static string Text(EntryRecord entry) => TextOf(entry.Model![0]) ?? "";

    private static string StopReason(EntryRecord entry) => S(((JsonObjectValue)entry.Model![0])["stopReason"]);

    private static async Task<(List<EntryRecord> Users, List<EntryRecord> Answers, List<EntryRecord> All)> Transcript(Recovery recovery)
    {
        var all = await AllEntries(recovery.Root);
        return ([.. all.Where(entry => entry.Kind == "pi.user")],
            [.. all.Where(entry => entry.Kind == "pi.assistant" && StopReason(entry) == "stop")], all);
    }

    private static async Task<List<SubmissionRecord>> Submissions(Recovery recovery) =>
        [.. (await recovery.Root.CommitAsync(async tx =>
        {
            var found = new List<SubmissionRecord>();
            foreach (var request in new[] { "r1", "r2" })
                if (await tx.GetSubmissionByRequestAsync(recovery.Root.Id, request) is { } record) found.Add(record);
            return found;
        }, Ctx))];

    private static async Task<List<TaskRecord>> Tasks(Recovery recovery) =>
        [.. (await recovery.Harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery(), 100), Ctx)).Items];

    /// <summary>Each submission is done, answered by the matching answer, in order: exactly one user entry and one answer per input.</summary>
    private static async Task Answered(Recovery recovery, params string[] inputs)
    {
        var (users, answers, _) = await Transcript(recovery);
        Seq(inputs, users.Select(Text), "user entries");
        Equal(inputs.Length, answers.Count, "answers");
        var submissions = await Submissions(recovery);
        Equal(inputs.Length, submissions.Count, "submissions");
        for (var index = 0; index < inputs.Length; index++)
        {
            var submission = submissions[index];
            Equal(SubmissionStatus.Done, submission.Status, $"submission {index + 1} status");
            Equal(users[index].Id, submission.Entry, $"submission {index + 1} entry");
            Equal(answers[index].Id, submission.Answer, $"submission {index + 1} answer");
        }
    }

    private static async Task ChatTurn(Recovery recovery, int? fauxCalls, int errors = 0)
    {
        await recovery.Idle();
        await Answered(recovery, "go");
        var (_, answers, all) = await Transcript(recovery);
        Equal("answer: go" + Padding, Text(answers[0]), "answer");
        Equal(errors, all.Count(entry => entry.Kind == "pi.assistant" && StopReason(entry) == "error"), "error entries");
        // A partial committed before the kill is converted into at most one aborted entry.
        Check(all.Count(entry => entry.Kind == "pi.assistant" && StopReason(entry) == "aborted") <= 1, "at most one converted partial");
        if (fauxCalls is { } calls) Equal(calls, recovery.FauxCalls, "requests after reopen");
        else Check(recovery.FauxCalls <= 1, "at most one request after reopen");
    }

    private static async Task ToolTurn(Recovery recovery, int runs, bool interrupted, int fauxCalls)
    {
        await recovery.Idle();
        await Answered(recovery, "go");
        var (_, answers, all) = await Transcript(recovery);
        Equal("done", Text(answers[0]), "final answer");
        var results = ToolsTests.Results(all);
        Equal(1, results.Count, "tool results");
        var text = TextOf(results[0]) ?? "";
        Equal(interrupted, results[0]["isError"] is JsonBooleanValue { Value: true }, "result isError");
        if (interrupted) Check(text.Contains("[error] Tool work was interrupted and may have partially run", StringComparison.Ordinal), "interrupted result: " + text);
        else Equal($"ran {runs}", text, "result text");
        Equal(runs, recovery.ToolRuns, "tool executions");
        Equal(fauxCalls, recovery.FauxCalls, "requests after reopen");
        var tool = (await Tasks(recovery)).Single(task => task.Kind == "pi.tool");
        MatchJson(interrupted ? """{"status":"failed"}""" : """{"status":"completed"}""", OutcomeJson(tool), "tool outcome");
    }

    private static async Task TwoTurns(Recovery recovery, int fauxCalls)
    {
        await recovery.Idle();
        await Answered(recovery, "one", "two");
        var (_, answers, _) = await Transcript(recovery);
        Seq(["answer: one", "answer: two"], answers.Select(Text), "answers");
        Equal(fauxCalls, recovery.FauxCalls, "requests after reopen");
    }

    private static async Task Resubmit(Recovery recovery, bool admitted)
    {
        var before = await recovery.ByRequest("r1");
        Equal(admitted, before is not null, "admitted before the kill");
        var submission = await recovery.Root.SubmitAsync(SubmissionDraft.Input("go", requestId: "r1"), Ctx);
        if (before is not null) Equal(before.Id, submission.Id, "retried submission id");
        Equal(SubmissionStatus.Done, (await submission.WaitAsync(Ctx).WaitAsync(RecoveryTimeout)).Status, "status");
        // Retried again once settled: still the same submission, and nothing runs again.
        var again = await recovery.Root.SubmitAsync(SubmissionDraft.Input("go", requestId: "r1"), Ctx);
        Equal(submission.Id, again.Id, "second retry id");
        await recovery.Idle();
        await Answered(recovery, "go");
        Equal(1, recovery.FauxCalls, "requests after reopen");
    }

    private static async Task ResubmitQueued(Recovery recovery)
    {
        var queued = await recovery.ByRequest("r2") ?? throw new InvalidOperationException("r2 was not admitted before the kill");
        Equal(SubmissionStatus.Queued, queued.Status, "r2 status before resume");
        var retried = await recovery.Root.SubmitAsync(SubmissionDraft.Input("two", WhenBusy.FollowUp, "r2"), Ctx);
        Equal(queued.Id, retried.Id, "retried submission id");
        await recovery.Idle();
        await Answered(recovery, "one", "two");
        Equal(2, recovery.FauxCalls, "requests after reopen");
    }

    private static async Task AbortedToolRound(Recovery recovery)
    {
        recovery.Harness.Resume();
        await recovery.Idle();
        var (users, answers, all) = await Transcript(recovery);
        Equal(1, users.Count, "user entries");
        Equal(0, answers.Count, "answers");
        var submission = (await Submissions(recovery)).Single();
        MatchJson("""{"status":"unanswered","reason":"aborted"}""", submission.ToJson(), "submission");
        Equal(1, recovery.ToolRuns, "tool executions");
        Equal(0, recovery.FauxCalls, "requests after reopen");
        var results = ToolsTests.Results(all);
        Equal(1, results.Count, "tool results");
        Check((TextOf(results[0]) ?? "").Contains("[error] Tool work was aborted", StringComparison.Ordinal), "aborted result: " + TextOf(results[0]));
        var tasks = await Tasks(recovery);
        var tool = tasks.Single(task => task.Kind == "pi.tool");
        var generation = tasks.Single(task => task.Kind == "pi.generation");
        MatchJson("""{"status":"aborted"}""", OutcomeJson(tool), "tool outcome");
        MatchJson("""{"status":"aborted"}""", OutcomeJson(generation), "generation outcome");
        // Bottom-up: the owned tool settles before its generation's abort handler ends the run.
        Check(tool.EndedAt <= generation.EndedAt, $"tool ended {tool.EndedAt} before generation {generation.EndedAt}");
    }

    private static async Task AbortedRequest(Recovery recovery)
    {
        recovery.Harness.Resume();
        await recovery.Idle();
        var (users, _, all) = await Transcript(recovery);
        Equal(1, users.Count, "user entries");
        Equal(0, all.Count(entry => entry.Kind == "pi.assistant"), "assistant entries");
        var submission = (await Submissions(recovery)).Single();
        MatchJson("""{"status":"unanswered","reason":"aborted"}""", submission.ToJson(), "submission");
        Equal(0, recovery.FauxCalls, "requests after reopen");
        var generation = (await Tasks(recovery)).Single(task => task.Kind == "pi.generation");
        MatchJson("""{"status":"aborted"}""", OutcomeJson(generation), "generation outcome");
    }
}
