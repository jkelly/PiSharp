using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Contracts;

// Decision 0011 (run W4, T4): agent compare never loses a run either. The built app runs as a child process (R5's CompareApp: --faux, a
// temporary data folder, a temporary project, git's global and system config switched off) and each case kills its whole process tree (no
// shutdown: a crash) in the middle of an agent compare run, then starts it again on the same data. Killed while two models' bash calls run
// (a "!" command that appends a marker line and then sleeps) and the third model's call still waits for approval: after the restart the two
// cut-off calls are not run again (each marker has one line) and their models go on to answer and be checked, the third asks again, and once
// allowed it runs once; a second crash in the middle of that call does not run it again either, and a third crash after the run finished
// changes nothing. Killed while every model streams its answer (a "slow:" task): after the restart each model's request is sent again under
// the same op, so each ends with one whole answer (after its cut-off start, kept and marked interrupted as a chat keeps it), one turn and one
// runs.db call row. The first case's project is a git repo (one worktree
// per model), the second's a plain folder (temporary copies). R5's own case covers a crash while every call waits for approval.
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> AgentRecoveryCases() =>
    [
        Case("agent-recovery.app.kill-mid-tool-runs-each-call-once-and-every-model-resumes", async () =>
        {
            await using var app = await CompareApp.StartAsync("acr-tools", ToolApprovalMode.Auto);
            await RecoveryGitAsync(app, "init", "-q", "-b", "main");
            await RecoveryGitAsync(app, "add", "-A");
            await RecoveryGitAsync(app, "commit", "-q", "-m", "base");
            var head = await RecoveryGitAsync(app, "rev-parse", "HEAD");
            var run = await app.CreateAsync(new JsonObject
            {
                ["requestId"] = "recover-tools-1",
                ["task"] = "!echo ran >> marker.txt && sleep 60",
                ["projectPath"] = app.Project,
                ["models"] = Models("echo", "echo-small", "echo-thinking"),
                ["approval"] = "ask",
                ["checkCommand"] = "echo checked >> check.txt",
            });
            var id = run.Id();
            var asked = await app.WaitAsync(id, status => status.Models().All(model => model.Str("waiting") == "approval"), "every model asks for its bash call");
            var folders = asked.Models().ToDictionary(model => model.Str("slot")!, model => model.Str("workingDirectory")!);
            Equal(3, folders.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "three workspaces");
            foreach (var folder in folders.Values)
            {
                Check(folder.StartsWith(Path.Join(app.Data, "workspaces"), StringComparison.OrdinalIgnoreCase), "each model works in its workspace: " + folder);
                Check(File.Exists(Path.Join(folder, ".git")), "a git worktree: " + folder);
            }
            string Marker(string slot) => Path.Join(folders[slot], "marker.txt");
            string Checked(string slot) => Path.Join(folders[slot], "check.txt");

            // A and B are allowed and start their calls; C's call still waits when the app dies.
            foreach (var slot in new[] { "a", "b" })
            {
                var call = asked.Models().Single(model => model.Str("slot") == slot)["approvals"]!.AsArray().Single()!;
                var allowed = await app.PostAsync($"/api/compare/agent/{id}/approve", new JsonObject { ["slot"] = slot, ["taskId"] = call["taskId"]!.GetValue<long>(), ["decision"] = "allow" });
                Equal(200, allowed.Status, $"{slot}'s call allowed: " + allowed.Text);
            }
            await WaitUntilAsync(() => Task.FromResult(Lines(Marker("a")).Length == 1 && Lines(Marker("b")).Length == 1), "A's and B's commands wrote their markers",
                TimeSpan.FromSeconds(45));
            var during = await app.StatusAsync(id);
            Equal("running", during.Str("state"), "the run runs (the commands sleep)");
            Equal("approval", during.Models().Single(model => model.Str("slot") == "c").Str("waiting"), "C still waits for its call");
            Equal(0, Lines(Marker("c")).Length, "C's command has not run");

            await app.KillAndRestartAsync();
            var resumed = await app.WaitAsync(id, status => status.Models().Where(model => model.Str("slot") is "a" or "b").All(model => model.Str("phase") == "done") &&
                status.Models().Single(model => model.Str("slot") == "c").Str("waiting") == "approval",
                "A and B go on after the restart and finish, and C asks again");
            Equal("running", resumed.Str("state"), "the run still runs");
            foreach (var slot in new[] { "a", "b" })
            {
                var model = resumed.Models().Single(item => item.Str("slot") == slot);
                Equal(1, Lines(Marker(slot)).Length, $"{slot}'s command ran once: the restart did not run it again");
                Equal("finished", model.Str("outcome"), $"{slot} finished: {model}");
                Check(model.Str("answer")!.Contains("Tool finished: bash", StringComparison.Ordinal), $"{slot} answered the cut-off result: " + model.Str("answer"));
                Equal(2, model["turns"]!.GetValue<int>(), $"{slot} took two turns, the call and the answer (none repeated)");
                Equal("passed", model["check"]?.Str("state"), $"{slot}'s check ran after it finished: {model["check"]}");
                Equal(1, Lines(Checked(slot)).Length, $"{slot}'s check ran once");
            }
            var c = resumed.Models().Single(model => model.Str("slot") == "c");
            Equal("echo ran >> marker.txt && sleep 60", c["approvals"]!.AsArray().Single()!.Str("command"), "C's same call asks again");
            Equal(0, Lines(Marker("c")).Length, "and has still not run");

            // C is allowed now, and the app dies again in the middle of C's call.
            var allowC = await app.PostAsync($"/api/compare/agent/{id}/approve", new JsonObject
            {
                ["slot"] = "c", ["taskId"] = c["approvals"]!.AsArray().Single()!["taskId"]!.GetValue<long>(), ["decision"] = "allow",
            });
            Equal(200, allowC.Status, "C's call allowed: " + allowC.Text);
            await WaitUntilAsync(() => Task.FromResult(Lines(Marker("c")).Length == 1), "C's command wrote its marker", TimeSpan.FromSeconds(45));
            Equal("running", (await app.StatusAsync(id)).Str("state"), "the run runs (C's command sleeps)");
            await app.KillAndRestartAsync();

            var finished = await app.WaitAsync(id, status => status.Str("state") == "finished", "the run finished after the second restart");
            foreach (var model in finished.Models())
            {
                var slot = model.Str("slot")!;
                Equal("finished", model.Str("outcome"), $"{slot} finished: {model}");
                Equal(1, Lines(Marker(slot)).Length, $"{slot}'s command ran exactly once");
                Equal(1, Lines(Checked(slot)).Length, $"{slot}'s check ran exactly once");
                Equal(2, model["turns"]!.GetValue<int>(), $"{slot}: two turns");
                Check(model["diff"]!["files"]!.GetValue<int>() >= 1, $"{slot}'s diff has its marker: {model["diff"]}");
            }
            Check(!File.Exists(Path.Join(app.Project, "marker.txt")), "the project itself is untouched");
            Equal(head, await RecoveryGitAsync(app, "rev-parse", "HEAD"), "nothing was committed to the project's branch");
            Equal("", await RecoveryGitAsync(app, "status", "--porcelain"), "and its working tree is clean");

            var runsDb = Path.Join(app.Data, "runs.db");
            Equal(("parent", "completed"), ReadDb(runsDb, $"SELECT kind, status FROM runs WHERE id = '{id}'", row => (row.GetString(0), row.GetString(1))).Single(),
                "one parent row, completed");
            foreach (var slot in new[] { "a", "b", "c" })
            {
                Equal("completed", ReadDb(runsDb, $"SELECT status FROM runs WHERE id = '{id}-{slot}'", row => row.GetString(0)).Single(), $"{slot}'s row completed");
                var calls = ReadDb(runsDb, $"SELECT op, status FROM runs WHERE parent_id = '{id}-{slot}' AND kind = 'call'", row => (row.GetString(0), row.GetString(1)));
                Equal(2, calls.Count, $"{slot}: one runs.db row per model call, none twice: {string.Join(", ", calls)}");
                Equal(2, calls.Select(call => call.Item1).Distinct(StringComparer.Ordinal).Count(), $"{slot}: each call under its own op");
                Check(calls.All(call => call.Item2 == "completed"), $"{slot}: every call completed: {string.Join(", ", calls)}");
            }

            // One more crash after the run ended: nothing runs again, and the run reads the same.
            await app.KillAndRestartAsync();
            var after = await app.WaitAsync(id, status => status.Str("state") == "finished", "the run is still finished");
            foreach (var model in after.Models())
            {
                var slot = model.Str("slot")!;
                Equal("finished", model.Str("outcome"), $"{slot} still finished");
                Equal(2, model["turns"]!.GetValue<int>(), $"{slot} took no new turn");
                Equal(1, Lines(Marker(slot)).Length, $"{slot}'s command still ran once");
                Equal(1, Lines(Checked(slot)).Length, $"{slot}'s check still ran once");
            }
        }),

        Case("agent-recovery.app.kill-mid-answer-gives-each-model-one-answer", async () =>
        {
            await using var app = await CompareApp.StartAsync("acr-answer", ToolApprovalMode.Ask);
            // The faux models answer "slow:" a word every 40 ms: 150 words take about 6 s, so the app dies while every model streams.
            var words = string.Join(' ', Enumerable.Range(1, 150).Select(index => $"w{index:000}"));
            var run = await app.CreateAsync(new JsonObject
            {
                ["requestId"] = "recover-answer-1",
                ["task"] = "slow: " + words,
                ["projectPath"] = app.Project,
                ["models"] = Models("echo", "echo-small"),
                ["isolation"] = "copy",
                ["approval"] = "ask",
            });
            var id = run.Id();
            var runsDb = Path.Join(app.Data, "runs.db");
            await WaitUntilAsync(() => Task.FromResult(File.Exists(runsDb) && ReadDb(runsDb,
                    $"SELECT COUNT(*) FROM runs WHERE parent_id IN ('{id}-a', '{id}-b') AND kind = 'call' AND status = 'running'", row => row.GetInt64(0)).Single() == 2),
                "both models' calls stream", TimeSpan.FromSeconds(45));
            var streaming = await app.StatusAsync(id);
            Equal("running", streaming.Str("state"), "the run runs");
            Check(streaming.Models().All(model => model.Str("phase") == "running"), "both models run: " + streaming.ToJsonString());
            await Task.Delay(800); // a few words in; the answers need about 6 s
            Check(ReadDb(runsDb, $"SELECT COUNT(*) FROM runs WHERE parent_id IN ('{id}-a', '{id}-b') AND kind = 'call' AND status = 'running'",
                row => row.GetInt64(0)).Single() == 2, "both calls still stream when the app dies");
            await app.KillAndRestartAsync();

            var finished = await app.WaitAsync(id, status => status.Str("state") == "finished", "the run finished after the restart");
            foreach (var model in finished.Models())
            {
                var slot = model.Str("slot")!;
                Equal("finished", model.Str("outcome"), $"{slot} finished: {model}");
                Equal(words, model.Str("answer"), $"{slot}'s answer is whole");
                Equal(1, model["turns"]!.GetValue<int>(), $"{slot}: the recovered request is the same turn");
                var calls = ReadDb(runsDb, $"SELECT op, status FROM runs WHERE parent_id = '{id}-{slot}' AND kind = 'call'", row => (row.GetString(0), row.GetString(1)));
                Equal(1, calls.Count, $"{slot}: the recovered call is one runs.db row: {string.Join(", ", calls)}");
                Equal("completed", calls.Single().Item2, $"{slot}: and it completed");
                var opened = await app.PostAsync($"/api/compare/agent/{id}/open-chat", new JsonObject { ["slot"] = slot });
                Equal(200, opened.Status, $"{slot} opens as a chat: " + opened.Text);
                var transcript = await app.App.Get($"/chat/{opened.Body!.Str("chatId")}/export.md");
                Equal(200, transcript.Status, $"{slot}'s session exports: " + transcript.Text);
                var text = transcript.Text.Replace("\r\n", "\n", StringComparison.Ordinal);
                Equal(1, Occurrences(text, "### You"), $"{slot}: the task was sent once: {text}");
                // As in a chat (RecoveryCases), the transcript keeps the cut-off start, marked as interrupted, then one whole answer.
                var answers = text.Split("### Agent", StringSplitOptions.None)[1..];
                Equal(1, answers.Count(answer => answer.Contains("w150", StringComparison.Ordinal)), $"{slot}: exactly one whole answer: {text}");
                Check(answers.Length <= 2 && answers[..^1].All(answer => answer.Contains("Interrupted", StringComparison.Ordinal) &&
                    !answer.Contains("w150", StringComparison.Ordinal)), $"{slot}: at most the cut-off start before it, marked interrupted: {text}");
                Check(answers[^1].Contains("w150", StringComparison.Ordinal), $"{slot}: the whole answer is the last: {text}");
            }
            Equal("completed", ReadDb(runsDb, $"SELECT status FROM runs WHERE id = '{id}'", row => row.GetString(0)).Single(), "the run's row completed");
        }),
    ];

    private static int Occurrences(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    /// <summary>Runs git in <paramref name="app"/>'s project with the app's own git settings (its empty global config, no system config) and
    /// a test identity, and returns its trimmed output (a failure throws).</summary>
    private static async Task<string> RecoveryGitAsync(CompareApp app, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = app.Project,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-c", "user.name=PiSharp Tests", "-c", "user.email=tests@pisharp.invalid", "-c", "commit.gpgsign=false" })
            info.ArgumentList.Add(argument);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["GIT_CONFIG_GLOBAL"] = Path.Join(app.Root, "gitconfig");
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {await error}");
        return (await output).Trim();
    }
}
